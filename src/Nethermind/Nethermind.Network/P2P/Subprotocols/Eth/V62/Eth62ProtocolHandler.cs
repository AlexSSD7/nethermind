//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public class Eth62ProtocolHandler : SyncPeerProtocolHandlerBase, IZeroProtocolHandler
    {
        private readonly TimeSpan _txFloodCheckInterval = TimeSpan.FromSeconds(60);
        private readonly Timer _txFloodCheckTimer;
        private bool _isDowngradedDueToTxFlooding;
        private bool _txFilteringDisabled;

        private readonly Random _random = new Random();
        private bool _statusReceived;

        public Eth62ProtocolHandler(
            ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager statsManager,
            ISyncServer syncServer,
            ITxPool txPool,
            ILogManager logManager) : base(session, serializer, statsManager, syncServer, txPool, logManager)
        {
            _txFloodCheckTimer = new Timer(_txFloodCheckInterval.TotalMilliseconds);
            _txFloodCheckTimer.Elapsed += CheckTxFlooding;
            _txFloodCheckTimer.Start();
        }

        public void DisableTxFiltering()
        {
            _txFilteringDisabled = true;
        }
        
        public override byte ProtocolVersion { get; protected set; } = 62;
        public override string ProtocolCode => Protocol.Eth;
        public override int MessageIdSpaceSize => 8;
        public override string Name => "eth62";
        protected override TimeSpan InitTimeout => Timeouts.Eth62Status;

        public override bool HasAvailableCapability(Capability capability) => false;
        public override bool HasAgreedCapability(Capability capability) => false;
        public override void AddSupportedCapability(Capability capability) { }

        public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;

        public override event EventHandler<ProtocolEventArgs> SubprotocolRequested
        {
            add { }
            remove { }
        }

        private void CheckTxFlooding(object sender, ElapsedEventArgs e)
        {
            if (!_isDowngradedDueToTxFlooding && _notAcceptedTxsSinceLastCheck / _txFloodCheckInterval.TotalSeconds > 10)
            {
                if (Logger.IsDebug) Logger.Debug($"Downgrading {Node.Id} due to tx flooding");
                _isDowngradedDueToTxFlooding = true;
            }
            else
            {
                if (_notAcceptedTxsSinceLastCheck / _txFloodCheckInterval.TotalSeconds > 100)
                {
                    if (Logger.IsDebug) Logger.Debug($"Disconnecting {Node.Id} due to tx flooding");
                    InitiateDisconnect(
                        DisconnectReason.UselessPeer,
                        $"tx flooding {_notAcceptedTxsSinceLastCheck}/{_txFloodCheckInterval.TotalSeconds > 100}");
                }
            }

            _notAcceptedTxsSinceLastCheck = 0;

            if (Session.IsClosing)
            {
                // this is an extra measure to avoid memory leak via timers
                OnDisposed();
            }
        }

        protected virtual void EnrichStatusMessage(StatusMessage statusMessage)
        {
        }
        
        public override void Init()
        {
            if (Logger.IsTrace) Logger.Trace(
                $"{ProtocolCode} v{ProtocolVersion} subprotocol initializing with {Session.Node:c}");
            if (SyncServer.Head == null)
            {
                throw new InvalidOperationException(
                    $"Cannot initialize {ProtocolCode} v{ProtocolVersion} protocol without the head block set");
            }

            BlockHeader head = SyncServer.Head;
            StatusMessage statusMessage = new StatusMessage();
            statusMessage.ChainId = (UInt256) SyncServer.ChainId;
            statusMessage.ProtocolVersion = ProtocolVersion;
            statusMessage.TotalDifficulty = head.TotalDifficulty ?? head.Difficulty;
            statusMessage.BestHash = head.Hash;
            statusMessage.GenesisHash = SyncServer.Genesis.Hash;
            EnrichStatusMessage(statusMessage);
            
            Metrics.StatusesSent++;
            Send(statusMessage);

            // We are expecting receiving Status message anytime from the p2p completion,
            // irrespective of sending Status from our side
            CheckProtocolInitTimeout().ContinueWith(x =>
            {
                if (x.IsFaulted && Logger.IsError)
                {
                    Logger.Error("Error during eth62Protocol handler timeout logic", x.Exception);
                }
            });
        }

        public override void HandleMessage(ZeroPacket message)
        {
            int packetType = message.PacketType;
            if (packetType != Eth62MessageCode.Status && !_statusReceived)
            {
                throw new SubprotocolException(
                    $"No {nameof(StatusMessage)} received prior to communication with {Session.Node:c}.");
            }

            int size = message.Content.ReadableBytes;
            if (Logger.IsTrace) Logger.Trace(
                $"{Counter:D5} {Eth62MessageCode.GetDescription(packetType)} from {Node:c}");
            
            switch (packetType)
            {
                case Eth62MessageCode.Status:
                    StatusMessage statusMsg = Deserialize<StatusMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(
                        Session.Node.Address, Name, statusMsg.ToString());
                    Handle(statusMsg);
                    break;
                case Eth62MessageCode.NewBlockHashes:
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(
                        Session.Node.Address, Name, nameof(NewBlockHashesMessage));
                    Handle(Deserialize<NewBlockHashesMessage>(message.Content));
                    break;
                case Eth62MessageCode.Transactions:
                    TransactionsMessage txMsg = Deserialize<TransactionsMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(
                        Session.Node.Address, Name, $"{nameof(TransactionsMessage)}({txMsg.Transactions.Count})");
                    Handle(txMsg);
                    break;
                case Eth62MessageCode.GetBlockHeaders:
                    GetBlockHeadersMessage msg = Deserialize<GetBlockHeadersMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(
                        Session.Node.Address,
                        Name,
                        $"{nameof(GetBlockHeadersMessage)}({msg.StartBlockNumber}|{msg.StartBlockHash}, {msg.MaxHeaders})");
                    Handle(msg);
                    break;
                case Eth62MessageCode.BlockHeaders:
                    BlockHeadersMessage headersMsg = Deserialize<BlockHeadersMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(
                        Session.Node.Address,
                        Name,
                        $"{nameof(BlockHeadersMessage)}({headersMsg.BlockHeaders.Length})");
                    Handle(headersMsg, size);
                    break;
                case Eth62MessageCode.GetBlockBodies:
                    GetBlockBodiesMessage getBodiesMsg = Deserialize<GetBlockBodiesMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(
                        Session.Node.Address,
                        Name,
                        $"{nameof(GetBlockBodiesMessage)}({getBodiesMsg.BlockHashes.Count})");
                    Handle(getBodiesMsg);
                    break;
                case Eth62MessageCode.BlockBodies:
                    BlockBodiesMessage bodiesMsg = Deserialize<BlockBodiesMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(
                        Session.Node.Address,
                        Name,
                        $"{nameof(BlockBodiesMessage)}({bodiesMsg.Bodies.Length})");
                    Handle(bodiesMsg, size);
                    break;
                case Eth62MessageCode.NewBlock:
                    NewBlockMessage newBlockMsg = Deserialize<NewBlockMessage>(message.Content);
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(
                        Session.Node.Address,
                        Name,
                        $"{nameof(NewBlockMessage)}({newBlockMsg.Block.Number})");
                    Handle(newBlockMsg);
                    break;
            }
        }

        private void Handle(StatusMessage status)
        {
            Metrics.StatusesReceived++;
            if (_statusReceived)
            {
                throw new SubprotocolException($"{nameof(StatusMessage)} has already been received in the past");
            }

            _statusReceived = true;
            if (Logger.IsTrace)
                Logger.Trace($"ETH received status from {Session.Node:c} with" +
                             Environment.NewLine + $" prot version\t{status.ProtocolVersion}" +
                             Environment.NewLine + $" network ID\t{status.ChainId}," +
                             Environment.NewLine + $" genesis hash\t{status.GenesisHash}," +
                             Environment.NewLine + $" best hash\t{status.BestHash}," +
                             Environment.NewLine + $" difficulty\t{status.TotalDifficulty}");

            _remoteHeadBlockHash = status.BestHash;
            
            ReceivedProtocolInitMsg(status);

            SyncPeerProtocolInitializedEventArgs eventArgs = new SyncPeerProtocolInitializedEventArgs(this)
            {
                ChainId = (long) status.ChainId,
                BestHash = status.BestHash,
                GenesisHash = status.GenesisHash,
                Protocol = status.Protocol,
                ProtocolVersion = status.ProtocolVersion,
                TotalDifficulty = status.TotalDifficulty
            };
            
            HeadHash = status.BestHash;
            TotalDifficulty = status.TotalDifficulty;
            ProtocolInitialized?.Invoke(this, eventArgs);
        }

        private long _notAcceptedTxsSinceLastCheck;

        protected void Handle(TransactionsMessage msg)
        {
            // TODO: disable that when IsMining is set to true
            if (!_txFilteringDisabled && (_isDowngradedDueToTxFlooding || 10 < _random.Next(0, 99)))
            {
                // we only accept 10% of transactions from downgraded nodes
                return;
            }
            
            Metrics.Eth62TransactionsReceived++;
            for (int i = 0; i < msg.Transactions.Count; i++)
            {
                Transaction tx = msg.Transactions[i];
                tx.DeliveredBy = Node.Id;
                tx.Timestamp = _timestamper.EpochSeconds;
                AddTxResult result = _txPool.AddTransaction(tx, TxHandlingOptions.None);
                if (result != AddTxResult.Added)
                {
                    _notAcceptedTxsSinceLastCheck++;
                }

                if (Logger.IsTrace) Logger.Trace(
                    $"{Node:c} sent {tx.Hash} tx and it was {result} (chain ID = {tx.Signature.ChainId})");
            }
        }

        private void Handle(NewBlockHashesMessage newBlockHashes)
        {
            Metrics.Eth62NewBlockHashesReceived++;
            foreach ((Keccak hash, long number) in newBlockHashes.BlockHashes)
            {
                SyncServer.HintBlock(hash, number, this);
            }
        }

        private void Handle(NewBlockMessage msg)
        {
            Metrics.Eth62NewBlockReceived++;
            if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(
                Session.Node.Address,
                Name,
                $"{nameof(NewBlockMessage)}({msg.Block.Number})");
            msg.Block.Header.TotalDifficulty = msg.TotalDifficulty;

            try
            {
                SyncServer.AddNewBlock(msg.Block, this);
            }
            catch (Exception e)
            {
                Logger.Debug($"Adding new block {msg.Block?.ToString(Block.Format.Short)} from {Node:c} failed: " + e.Message);
                throw;
            }
        }

        public override void NotifyOfNewBlock(Block block, SendBlockPriority priority)
        {
            switch (priority)
            {
                case SendBlockPriority.High:
                    SendNewBlock(block);
                    break;
                case SendBlockPriority.Low:
                    HintNewBlock(block.Hash, block.Number);
                    break;
                default:
                    Logger.Error(
                        $"Unknown priority ({priority.ToString()}) passed to {nameof(NotifyOfNewBlock)} - handling as low priority");
                    HintNewBlock(block.Hash, block.Number);
                    break;
            }
        }

        private void SendNewBlock(Block block)
        {
            if (!block.TotalDifficulty.HasValue)
            {
                throw new InvalidOperationException($"Trying to send a block {block.Hash} with null total difficulty");
            }
            
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} NewBlock to {Node:c}");
            
            NewBlockMessage msg = new NewBlockMessage();
            msg.Block = block;
            msg.TotalDifficulty = block.TotalDifficulty.Value;

            Send(msg);
        }

        private void HintNewBlock(Keccak blockHash, long number)
        {
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} HintBlock to {Node:c}");

            NewBlockHashesMessage msg = new NewBlockHashesMessage();
            msg.BlockHashes = new[] { (blockHash, number) };
            Send(msg);
        }

        protected override void OnDisposed()
        {
            try
            {
                _txFloodCheckTimer.Elapsed -= CheckTxFlooding;
                _txFloodCheckTimer.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (NullReferenceException)
            {
            }
        }
    }
}
