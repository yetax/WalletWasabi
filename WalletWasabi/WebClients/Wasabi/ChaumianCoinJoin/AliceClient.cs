﻿using NBitcoin;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using WalletWasabi.Backend.Models;
using WalletWasabi.Backend.Models.Requests;
using WalletWasabi.Backend.Models.Responses;
using WalletWasabi.Logging;
using WalletWasabi.Bases;
using System.Threading;
using WalletWasabi.Exceptions;
using WalletWasabi.Models.ChaumianCoinJoin;
using NBitcoin.BouncyCastle.Math;
using NBitcoin.Crypto;
using static NBitcoin.Crypto.SchnorrBlinding;

namespace WalletWasabi.WebClients.Wasabi.ChaumianCoinJoin
{
	public class AliceClient : TorDisposableBase
	{
		public long RoundId { get; private set; }
		public Guid UniqueId { get; private set; }
		public Network Network { get; }

		public BitcoinAddress[] RegisteredAddresses { get; set; }
		public SchnorrPubKey[] SchnorrPubKeys { get; set; }
		public Requester[] Requesters { get; set; }
		public uint256[] OutputScriptHashes { get; set; }

		/// <inheritdoc/>
		private AliceClient(Network network, Uri baseUri, IPEndPoint torSocks5EndPoint = null) : base(baseUri, torSocks5EndPoint)
		{
			Network = network;
		}

		public static async Task<AliceClient> CreateNewAsync(Network network, InputsRequest request, Uri baseUri, IPEndPoint torSocks5EndPoint = null)
		{
			AliceClient client = new AliceClient(network, baseUri, torSocks5EndPoint);
			try
			{
				using (HttpResponseMessage response = await client.TorClient.SendAsync(HttpMethod.Post, $"/api/v{Helpers.Constants.BackendMajorVersion}/btc/chaumiancoinjoin/inputs/", request.ToHttpStringContent()))
				{
					if (response.StatusCode != HttpStatusCode.OK)
					{
						await response.ThrowRequestExceptionFromContentAsync();
					}

					var inputsResponse = await response.Content.ReadAsJsonAsync<InputsResponse>();

					client.RoundId = inputsResponse.RoundId;
					client.UniqueId = inputsResponse.UniqueId;
					Logger.LogInfo<AliceClient>($"Round ({client.RoundId}), Alice ({client.UniqueId}): Registered {request.Inputs.Count()} inputs.");

					return client;
				}
			}
			catch
			{
				client.Dispose();
				throw;
			}
		}

		public static async Task<AliceClient> CreateNewAsync(Network network, BitcoinAddress changeOutput, IEnumerable<uint256> blindedOutputScriptHashes, IEnumerable<InputProofModel> inputs, Uri baseUri, IPEndPoint torSocks5EndPoint = null)
		{
			var request = new InputsRequest
			{
				BlindedOutputScripts = blindedOutputScriptHashes,
				ChangeOutputAddress = changeOutput,
				Inputs = inputs
			};
			return await CreateNewAsync(network, request, baseUri, torSocks5EndPoint);
		}

		public async Task<(CcjRoundPhase currentPhase, IEnumerable<(BitcoinAddress output, UnblindedSignature signature, int level)> activeOutputs)> PostConfirmationAsync()
		{
			using (HttpResponseMessage response = await TorClient.SendAsync(HttpMethod.Post, $"/api/v{Helpers.Constants.BackendMajorVersion}/btc/chaumiancoinjoin/confirmation?uniqueId={UniqueId}&roundId={RoundId}"))
			{
				if (response.StatusCode != HttpStatusCode.OK)
				{
					await response.ThrowRequestExceptionFromContentAsync();
				}

				ConnConfResp resp = await response.Content.ReadAsJsonAsync<ConnConfResp>();
				Logger.LogInfo<AliceClient>($"Round ({RoundId}), Alice ({UniqueId}): Confirmed connection. Phase: {resp.CurrentPhase}.");

				var activeOutputs = new List<(BitcoinAddress output, UnblindedSignature signature, int level)>();
				if (resp.BlindedOutputSignatures != null && resp.BlindedOutputSignatures.Any())
				{
					var unblindedSignatures = new List<UnblindedSignature>();
					var blindedSignatures = resp.BlindedOutputSignatures.ToArray();
					for (int i = 0; i < blindedSignatures.Length; i++)
					{
						uint256 blindedSignature = blindedSignatures[i];
						Requester requester = Requesters[i];
						UnblindedSignature unblindedSignature = requester.UnblindSignature(blindedSignature);

						uint256 outputScriptHash = OutputScriptHashes[i];
						PubKey signerPubKey = SchnorrPubKeys[i].SignerPubKey;
						if (!VerifySignature(outputScriptHash, unblindedSignature, signerPubKey))
						{
							throw new NotSupportedException($"Coordinator did not sign the blinded output properly for level: {i}.");
						}

						unblindedSignatures.Add(unblindedSignature);
					}

					for (int i = 0; i < Math.Min(unblindedSignatures.Count, RegisteredAddresses.Length); i++)
					{
						var sig = unblindedSignatures[i];
						var addr = RegisteredAddresses[i];
						var lvl = i;
						activeOutputs.Add((addr, sig, lvl));
					}
				}

				return (resp.CurrentPhase, activeOutputs);
			}
		}

		public async Task PostUnConfirmationAsync()
		{
			using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
			{
				try
				{
					using (HttpResponseMessage response = await TorClient.SendAsync(HttpMethod.Post, $"/api/v{Helpers.Constants.BackendMajorVersion}/btc/chaumiancoinjoin/unconfirmation?uniqueId={UniqueId}&roundId={RoundId}", cancel: cts.Token))
					{
						if (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Gone) // Otherwise maybe some internet connection issue there's. Let's consider that as timed out.
						{
							await response.ThrowRequestExceptionFromContentAsync();
						}
					}
				}
				catch (TaskCanceledException) // If couldn't do it within 3 seconds then it'll likely time out and take it as unconfirmed.
				{
					return;
				}
				catch (OperationCanceledException) // If couldn't do it within 3 seconds then it'll likely time out and take it as unconfirmed.
				{
					return;
				}
				catch (TimeoutException) // If couldn't do it within 3 seconds then it'll likely time out and take it as unconfirmed.
				{
					return;
				}
				catch (ConnectionException)  // If some internet connection issue then it'll likely time out and take it as unconfirmed.
				{
					return;
				}
				catch (TorSocks5FailureResponseException) // If some Tor connection issue then it'll likely time out and take it as unconfirmed.
				{
					return;
				}
			}
			Logger.LogInfo<AliceClient>($"Round ({RoundId}), Alice ({UniqueId}): Unconfirmed connection.");
		}

		public async Task<Transaction> GetUnsignedCoinJoinAsync()
		{
			using (HttpResponseMessage response = await TorClient.SendAsync(HttpMethod.Get, $"/api/v{Helpers.Constants.BackendMajorVersion}/btc/chaumiancoinjoin/coinjoin?uniqueId={UniqueId}&roundId={RoundId}"))
			{
				if (response.StatusCode != HttpStatusCode.OK)
				{
					await response.ThrowRequestExceptionFromContentAsync();
				}

				var coinjoinHex = await response.Content.ReadAsJsonAsync<string>();

				Transaction coinJoin = Transaction.Parse(coinjoinHex, Network.Main);
				Logger.LogInfo<AliceClient>($"Round ({RoundId}), Alice ({UniqueId}): Acquired unsigned CoinJoin: {coinJoin.GetHash()}.");
				return coinJoin;
			}
		}

		public async Task PostSignaturesAsync(IDictionary<int, WitScript> signatures)
		{
			var myDic = signatures.ToDictionary(signature => signature.Key, signature => signature.Value.ToString());

			var jsonSignatures = JsonConvert.SerializeObject(myDic, Formatting.None);
			var signatureRequestContent = new StringContent(jsonSignatures, Encoding.UTF8, "application/json");

			using (HttpResponseMessage response = await TorClient.SendAsync(HttpMethod.Post, $"/api/v{Helpers.Constants.BackendMajorVersion}/btc/chaumiancoinjoin/signatures?uniqueId={UniqueId}&roundId={RoundId}", signatureRequestContent))
			{
				if (response.StatusCode != HttpStatusCode.NoContent)
				{
					await response.ThrowRequestExceptionFromContentAsync();
				}
				Logger.LogInfo<AliceClient>($"Round ({RoundId}), Alice ({UniqueId}): Posted {signatures.Count} signatures.");
			}
		}
	}
}
