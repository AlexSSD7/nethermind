﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections;
using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db
{
    public class RocksDbProvider : RocksDbProviderBase, IDbProvider
    {
        public RocksDbProvider(IDbsConfig dbsConfig, ILogManager logManager, bool useTraceDb, bool useReceiptsDb) : base(dbsConfig, logManager)
        {
            BlocksDb = CreatePartDb(DbParts.Blocks);
            HeadersDb = CreatePartDb(DbParts.Headers);
            BlockInfosDb = CreatePartDb(DbParts.BlockInfos);
            StateDb = new StateDb(CreatePartDb(DbParts.State));
            CodeDb = new StateDb(CreatePartDb(DbParts.Code));
            PendingTxsDb = CreatePartDb(DbParts.PendingTxs);
            ConfigsDb = CreatePartDb(DbParts.Configs);
            EthRequestsDb = CreatePartDb(DbParts.EthRequests);
            
            if (useReceiptsDb)
            {
                ReceiptsDb = CreatePartDb(DbParts.Receipts);
            }
            else
            {
                ReceiptsDb = new ReadOnlyDb(new MemDb(), false);
            }
            
            if (useTraceDb)
            {
                TraceDb = CreatePartDb(DbParts.Trace);
            }
            else
            {
                TraceDb = new ReadOnlyDb(new MemDb(), false);
            }
        }

        public ISnapshotableDb StateDb { get; }
        public ISnapshotableDb CodeDb { get; }
        public IDb TraceDb { get; }
        public IDb ReceiptsDb { get; }
        public IDb BlocksDb { get; }
        public IDb HeadersDb { get; }
        public IDb BlockInfosDb { get; }
        public IDb PendingTxsDb { get; }
        public IDb ConfigsDb { get; }
        public IDb EthRequestsDb { get; }

        public override void Dispose()
        {
            StateDb?.Dispose();
            CodeDb?.Dispose();
            ReceiptsDb?.Dispose();
            BlocksDb?.Dispose();
            HeadersDb?.Dispose();
            BlockInfosDb?.Dispose();
            PendingTxsDb?.Dispose();
            TraceDb?.Dispose();
            ConfigsDb?.Dispose();
            EthRequestsDb?.Dispose();
        }
    }
}