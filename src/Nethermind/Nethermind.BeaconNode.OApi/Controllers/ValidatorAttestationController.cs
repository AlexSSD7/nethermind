using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.OApi.Controllers
{
    [ApiController]
    [Route("/validator/attestation")]
    public class ValidatorAttestationController : ControllerBase
    {
        private readonly IBeaconNodeApi _beaconNode;
        private readonly ILogger _logger;

        public ValidatorAttestationController(ILogger<ValidatorAttestationController> logger, IBeaconNodeApi beaconNode)
        {
            _logger = logger;
            _beaconNode = beaconNode;
        }

        /// <summary>Produce an attestation, without signature.</summary>
        /// <remarks>
        /// <para>
        /// Requests that the beacon node produce an Attestation, with a blank signature field, which the validator will then sign.
        /// </para>
        /// </remarks>
        /// <param name="validator_pubkey">Uniquely identifying which validator this attestation is to be produced for.</param>
        /// <param name="poc_bit">The proof-of-custody bit that is to be reported by the requesting validator. This bit will be inserted into the appropriate location in the returned `Attestation`.</param>
        /// <param name="slot">The slot for which the attestation should be proposed.</param>
        /// <param name="shard">The shard number for which the attestation is to be proposed.</param>
        [HttpGet]
        // ReSharper disable once InconsistentNaming
        // ReSharper disable once IdentifierTypo
        public async Task<IActionResult> GetAsync([FromQuery] byte[] validator_pubkey, [FromQuery] uint poc_bit,
            [FromQuery] ulong slot, [FromQuery] ulong shard,
            CancellationToken cancellationToken)
        {
            if (_logger.IsDebug())
                LogDebug.NewAttestationRequested(_logger, slot, shard, Bytes.ToHexString(validator_pubkey), null);

            BlsPublicKey validatorPublicKey = new BlsPublicKey(validator_pubkey);
            bool proofOfCustodyBit = poc_bit > 0;
            Slot targetSlot = new Slot(slot);
            Shard targetShard = new Shard(shard);

            ApiResponse<Attestation> apiResponse =
                await _beaconNode
                    .NewAttestationAsync(validatorPublicKey, proofOfCustodyBit, targetSlot, targetShard,
                        cancellationToken).ConfigureAwait(false);

            switch (apiResponse.StatusCode)
            {
                case Core2.Api.StatusCode.Success:
                    return Ok(apiResponse.Content);
                case Core2.Api.StatusCode.InvalidRequest:
                    return Problem("Invalid request syntax.", statusCode: (int) apiResponse.StatusCode);
                case Core2.Api.StatusCode.CurrentlySyncing:
                    return Problem("Beacon node is currently syncing, try again later.",
                        statusCode: (int) apiResponse.StatusCode);
            }

            return Problem("Beacon node internal error.", statusCode: (int) apiResponse.StatusCode);
        }
    }
}