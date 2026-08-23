using System.Numerics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nethereum.Signer;
using Nethereum.Signer.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace H2HClientWeb.Services;

public sealed class TronTestnetClient
{
    private const string NileUrl = "https://nile.trongrid.io";
    private const string NileUsdtContract = "TXYZopYRdj2D9XRtbG411XZZ3kM5VkAeBf";
    private readonly HttpClient _http = new() { BaseAddress = new Uri(NileUrl) };

    public async Task<TronTransferResult> SendUsdtAsync(
        string privateKey,
        string recipientAddress,
        decimal amount,
        CancellationToken ct = default)
    {
        try
        {
            privateKey = NormalizePrivateKey(privateKey);
            var amountSunDecimal = decimal.Round(amount * 1_000_000m, 0, MidpointRounding.AwayFromZero);
            if (amountSunDecimal <= 0 || amountSunDecimal > long.MaxValue)
                return new(false, null, "Некорректная сумма USDT.");

            var senderAddress = GetTronAddress(privateKey);
            var parameter = AbiEncodeTransfer(recipientAddress, (long)amountSunDecimal);
            var requestBody = new
            {
                owner_address = HexFromTronAddress(senderAddress),
                contract_address = HexFromTronAddress(NileUsdtContract),
                function_selector = "transfer(address,uint256)",
                parameter,
                fee_limit = 150_000_000,
                call_value = 0
            };

            var triggerResponse = await PostAsync("/wallet/triggersmartcontract", requestBody, ct);
            if (!IsResultSuccessful(triggerResponse))
                return new(false, null, "TronGrid не создал транзакцию: " + GetNodeError(triggerResponse));

            if (!triggerResponse.TryGetProperty("transaction", out var transaction))
                return new(false, null, "TronGrid не вернул объект транзакции.");

            var txId = transaction.GetProperty("txID").GetString();
            var rawDataHex = transaction.GetProperty("raw_data_hex").GetString();
            if (string.IsNullOrWhiteSpace(txId) || string.IsNullOrWhiteSpace(rawDataHex))
                return new(false, null, "TronGrid вернул неполную транзакцию.");

            var rawData = Convert.FromHexString(rawDataHex);
            var signature = SignTransaction(rawData, privateKey);

            var rawDataJson = transaction.GetProperty("raw_data").GetRawText();
            using var rawDataDocument = JsonDocument.Parse(rawDataJson);
            var broadcastBody = new
            {
                txID = txId,
                raw_data = rawDataDocument.RootElement,
                raw_data_hex = rawDataHex,
                signature = new[] { signature }
            };

            var broadcastResponse = await PostAsync("/wallet/broadcasttransaction", broadcastBody, ct);
            if (!IsResultSuccessful(broadcastResponse))
                return new(false, txId, "Broadcast отклонён: " + GetNodeError(broadcastResponse));

            return await WaitForConfirmationAsync(txId, ct);
        }
        catch (Exception ex)
        {
            return new(false, null, ex.Message);
        }
    }

    private async Task<TronTransferResult> WaitForConfirmationAsync(string txId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var info = await PostAsync("/walletsolidity/gettransactioninfobyid", new { value = txId }, ct);
            if (info.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
            {
                if (info.TryGetProperty("receipt", out var receipt) &&
                    receipt.TryGetProperty("result", out var receiptResult) &&
                    string.Equals(receiptResult.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    return new(true, txId, null);
                }

                return new(false, txId, "Транзакция завершилась ошибкой: " + GetNodeError(info));
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        return new(false, txId, "Транзакция отправлена, но не подтверждена за 60 секунд.");
    }

    private async Task<JsonElement> PostAsync(string path, object body, CancellationToken ct)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        using var response = await _http.PostAsync(path, content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static bool IsResultSuccessful(JsonElement root) =>
        root.TryGetProperty("result", out var result) &&
        (result.ValueKind == JsonValueKind.True ||
         (result.ValueKind == JsonValueKind.Object &&
          result.TryGetProperty("result", out var nested) && nested.GetBoolean()));

    private static string GetNodeError(JsonElement root)
    {
        if (root.TryGetProperty("resMessage", out var hexMessage))
            return DecodeHex(hexMessage.GetString());
        if (root.TryGetProperty("message", out var message))
            return DecodeBase64(message.GetString());
        if (root.TryGetProperty("result", out var result))
            return result.ToString();
        return root.ToString();
    }

    private static string NormalizePrivateKey(string privateKey)
    {
        privateKey = privateKey.Trim();
        if (privateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            privateKey = privateKey[2..];
        if (privateKey.Length != 64 || !privateKey.All(Uri.IsHexDigit))
            throw new FormatException("Приватный ключ должен содержать 64 hex-символа.");
        return privateKey;
    }

    private static string GetTronAddress(string privateKey)
    {
        var key = new EthECKey(privateKey);
        var ethAddress = key.GetPublicAddress();
        var payload = new byte[21];
        payload[0] = 0x41;
        Convert.FromHexString(ethAddress[2..]).CopyTo(payload, 1);
        return Base58CheckEncode(payload);
    }

    private static string HexFromTronAddress(string address) =>
        Convert.ToHexString(Base58CheckDecode(address)).ToLowerInvariant();

    private static string AbiEncodeTransfer(string recipientAddress, long amountSun)
    {
        var addressBytes = Base58CheckDecode(recipientAddress)[1..];
        if (addressBytes.Length != 20)
            throw new FormatException("Некорректный TRON-адрес получателя.");

        var addressParameter = new byte[32];
        addressBytes.CopyTo(addressParameter, 12);
        var amountParameter = new byte[32];
        var amountBytes = new BigInteger(amountSun).ToByteArray(isUnsigned: true, isBigEndian: true);
        amountBytes.CopyTo(amountParameter, 32 - amountBytes.Length);
        return Convert.ToHexString(addressParameter).ToLowerInvariant() +
               Convert.ToHexString(amountParameter).ToLowerInvariant();
    }

    private static string SignTransaction(byte[] rawData, string privateKey)
    {
        var hash = SHA256.HashData(rawData);
        var privateKeyBytes = Convert.FromHexString(privateKey);
        var privateKeyInteger = new Org.BouncyCastle.Math.BigInteger(1, privateKeyBytes);
        var parameters = new ECPrivateKeyParameters(privateKeyInteger, ECKey.CURVE);
        var signer = new ECDsaSigner(new HMacDsaKCalculator(new Sha256Digest()));
        signer.Init(true, parameters);
        var components = signer.GenerateSignature(hash);

        var r = PadLeft(components[0].ToByteArrayUnsigned(), 32);
        var s = PadLeft(components[1].ToByteArrayUnsigned(), 32);
        var expectedPublicKey = new EthECKey(privateKey).GetPubKeyNoPrefix();
        var signature = new ECDSASignature(components[0], components[1]);
        byte recoveryId = 0;

        for (byte candidate = 0; candidate <= 1; candidate++)
        {
            var recovered = ECKey.RecoverFromSignature(candidate, signature, hash, false)?.GetPubKey(false);
            if (recovered is not null && recovered.Skip(1).SequenceEqual(expectedPublicKey))
            {
                recoveryId = candidate;
                break;
            }
        }

        var result = new byte[65];
        r.CopyTo(result, 0);
        s.CopyTo(result, 32);
        result[64] = recoveryId;
        return Convert.ToHexString(result).ToLowerInvariant();
    }

    private static byte[] Base58CheckDecode(string input)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var number = BigInteger.Zero;
        foreach (var character in input)
        {
            var digit = alphabet.IndexOf(character);
            if (digit < 0) throw new FormatException("Некорректный Base58-адрес.");
            number = number * 58 + digit;
        }

        var bytes = number.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = input.TakeWhile(character => character == '1').Count();
        var data = new byte[leadingZeros + bytes.Length];
        bytes.CopyTo(data, leadingZeros);
        if (data.Length != 25) throw new FormatException("Некорректная длина TRON-адреса.");

        var payload = data[..^4];
        var expectedChecksum = SHA256.HashData(SHA256.HashData(payload))[..4];
        if (!data[^4..].SequenceEqual(expectedChecksum))
            throw new FormatException("Некорректная контрольная сумма TRON-адреса.");
        return payload;
    }

    private static string Base58CheckEncode(byte[] payload)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var checksum = SHA256.HashData(SHA256.HashData(payload))[..4];
        var data = payload.Concat(checksum).ToArray();
        var number = new BigInteger(data, isUnsigned: true, isBigEndian: true);
        var result = new StringBuilder();
        while (number > 0)
        {
            result.Insert(0, alphabet[(int)(number % 58)]);
            number /= 58;
        }
        foreach (var value in data)
        {
            if (value != 0) break;
            result.Insert(0, '1');
        }
        return result.ToString();
    }

    private static byte[] PadLeft(byte[] bytes, int length)
    {
        if (bytes.Length >= length) return bytes;
        var result = new byte[length];
        bytes.CopyTo(result, length - bytes.Length);
        return result;
    }

    private static string DecodeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown error";
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException) { return value; }
    }

    private static string DecodeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown error";
        try { return Encoding.UTF8.GetString(Convert.FromHexString(value)); }
        catch (FormatException) { return value; }
    }
}

public record TronTransferResult(bool Success, string? TxId, string? Error);
