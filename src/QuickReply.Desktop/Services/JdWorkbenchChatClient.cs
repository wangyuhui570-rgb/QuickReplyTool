using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class JdWorkbenchChatClient : IJdWorkbenchChatClient
{
    private const string KfBaseUrl = "https://kf.jd.com";
    private readonly HttpClient _httpClient;
    private readonly HttpClientHandler _handler;
    private bool _disposed;

    public JdWorkbenchChatClient(JdWorkbenchProfile profile, string tempRootPath)
    {
        _handler = new HttpClientHandler
        {
            CookieContainer = BuildCookieContainer(profile.ProfilePath, tempRootPath),
            AutomaticDecompression = DecompressionMethods.All
        };
        _httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri($"{KfBaseUrl}/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://xi.jd.com/kf-manage-lite/");
    }

    public async Task<IReadOnlyList<JdChatSession>> QuerySessionsAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken)
    {
        var path = "waiterSession/jingmai/queryList";
        var query = $"?StartTime={Uri.EscapeDataString(startTime.ToString("yyyy-MM-dd HH:mm:ss"))}&EndTime={Uri.EscapeDataString(endTime.ToString("yyyy-MM-dd HH:mm:ss"))}&pageSize=500";
        using var response = await _httpClient.GetAsync(path + query, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(document.RootElement, "queryList");

        if (!document.RootElement.TryGetProperty("waiterSessionList", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return list.EnumerateArray().Select(ReadSession).Where(session => !string.IsNullOrWhiteSpace(session.Cid)).ToArray();
    }

    public async Task<IReadOnlyList<JdChatMessage>> QueryChatMessagesAsync(string cid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cid))
        {
            return [];
        }

        var query = $"?cid={Uri.EscapeDataString(cid)}";
        using var response = await _httpClient.GetAsync("waiterSession/queryChatLog" + query, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(document.RootElement, "queryChatLog");

        var messages = new List<JdChatMessage>();
        if (!document.RootElement.TryGetProperty("chatLogList", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var block in blocks.EnumerateArray())
        {
            if (!block.TryGetProperty("chatLogMessageList", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                messages.Add(ReadMessage(cid, item));
            }
        }

        return messages
            .OrderBy(message => message.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(message => message.MessageKey, StringComparer.Ordinal)
            .ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient.Dispose();
        _handler.Dispose();
        _disposed = true;
    }

    private static CookieContainer BuildCookieContainer(string profilePath, string tempRootPath)
    {
        var cefCachePath = Path.Combine(profilePath, ".cefcache");
        var prefsPath = Path.Combine(cefCachePath, "LocalPrefs.json");
        var cookiesPath = Path.Combine(cefCachePath, "Network", "Cookies");
        if (!File.Exists(prefsPath) || !File.Exists(cookiesPath))
        {
            throw new FileNotFoundException("京东工作台 Cookie 或 LocalPrefs 不存在。");
        }

        using var prefsDocument = JsonDocument.Parse(File.ReadAllText(prefsPath, Encoding.UTF8));
        var encryptedKey = prefsDocument.RootElement
            .GetProperty("os_crypt")
            .GetProperty("encrypted_key")
            .GetString();
        if (string.IsNullOrWhiteSpace(encryptedKey))
        {
            throw new InvalidOperationException("京东工作台 LocalPrefs 缺少 Chromium 加密密钥。");
        }

        var keyBytes = Convert.FromBase64String(encryptedKey);
        var prefix = Encoding.ASCII.GetBytes("DPAPI");
        if (keyBytes.Length <= prefix.Length || !keyBytes.Take(prefix.Length).SequenceEqual(prefix))
        {
            throw new InvalidOperationException("京东工作台 Chromium 加密密钥格式异常。");
        }

        var masterKey = ProtectedData.Unprotect(keyBytes[prefix.Length..], optionalEntropy: null, DataProtectionScope.CurrentUser);
        var tempDirectory = Path.Combine(tempRootPath, "jd-workbench");
        Directory.CreateDirectory(tempDirectory);
        var tempCookiePath = Path.Combine(tempDirectory, $"cookies-{Guid.NewGuid():N}.db");
        File.Copy(cookiesPath, tempCookiePath, overwrite: true);

        try
        {
            return ReadCookies(tempCookiePath, masterKey);
        }
        finally
        {
            TryDelete(tempCookiePath);
        }
    }

    private static CookieContainer ReadCookies(string cookieDatabasePath, byte[] masterKey)
    {
        var container = new CookieContainer();
        using var connection = new SqliteConnection($"Data Source={cookieDatabasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT host_key, name, path, encrypted_value FROM cookies WHERE host_key LIKE '%jd.com%';";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var host = reader.GetString(0);
            var name = reader.GetString(1);
            var path = reader.IsDBNull(2) ? "/" : reader.GetString(2);
            var encryptedValue = reader.GetFieldValue<byte[]>(3);
            var value = TryDecryptCookie(encryptedValue, masterKey);
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(name) || value is null)
            {
                continue;
            }

            container.Add(new Cookie(name, value, string.IsNullOrWhiteSpace(path) ? "/" : path, host));
        }

        return container;
    }

    private static string? TryDecryptCookie(byte[] encryptedValue, byte[] masterKey)
    {
        try
        {
            if (encryptedValue.Length > 31 &&
                encryptedValue[0] == (byte)'v' &&
                encryptedValue[1] is (byte)'1' &&
                encryptedValue[2] is (byte)'0' or (byte)'1')
            {
                var nonce = encryptedValue[3..15];
                var cipherWithTag = encryptedValue[15..];
                var cipher = cipherWithTag[..^16];
                var tag = cipherWithTag[^16..];
                var plain = new byte[cipher.Length];
#pragma warning disable SYSLIB0053
                using var aes = new AesGcm(masterKey);
#pragma warning restore SYSLIB0053
                aes.Decrypt(nonce, cipher, tag, plain);
                return Encoding.UTF8.GetString(plain);
            }

            var unprotected = ProtectedData.Unprotect(encryptedValue, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(unprotected);
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureSuccess(JsonElement root, string operation)
    {
        var code = TryGetString(root, "code");
        if (string.Equals(code, "success", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var message = TryGetString(root, "msg");
        throw new InvalidOperationException($"{operation} failed: {message}".Trim());
    }

    private static JdChatSession ReadSession(JsonElement item)
    {
        var cid = TryGetString(item, "cid");
        var responseTime = TryGetString(item, "responseTime");
        var consultationDate = TryGetString(item, "consultationDate");
        var closeDate = TryGetString(item, "closeDate");
        var customerMsgNum = TryGetString(item, "customerMsgNum");
        var serviceMsgNum = TryGetString(item, "serviceMsgNum");
        var transferStatus = TryGetString(item, "transferStatus");
        return new JdChatSession
        {
            Cid = cid,
            Customer = TryGetString(item, "customer"),
            GoodsName = TryGetString(item, "goodsName"),
            Service = TryGetString(item, "service"),
            ConsultationDate = consultationDate,
            RawMarker = string.Join("|", responseTime, consultationDate, closeDate, customerMsgNum, serviceMsgNum, transferStatus)
        };
    }

    private static JdChatMessage ReadMessage(string cid, JsonElement item)
    {
        var created = TryGetString(item, "created");
        var speaker = TryGetBool(item, "waiterSend") ? "waiter" : "customer";
        var messageType = TryGetString(item, "type");
        var content = TryGetString(item, "content");
        var createdAt = TryParseDateTimeOffset(created);
        var messageKey = BuildMessageKey(cid, item, speaker, content, createdAt);
        return new JdChatMessage
        {
            MessageKey = messageKey,
            Cid = cid,
            Speaker = speaker,
            MessageType = messageType,
            Content = content,
            CreatedAt = createdAt
        };
    }

    internal static string BuildMessageKeyForTesting(string cid, string mid, string sid, DateTimeOffset? createdAt, string speaker, string content)
    {
        if (!string.IsNullOrWhiteSpace(mid))
        {
            return $"{cid}|mid:{mid}";
        }

        var raw = string.Join("|", cid, sid, createdAt?.ToString("O") ?? string.Empty, speaker, content);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
        return $"{cid}|hash:{hash}";
    }

    private static string BuildMessageKey(string cid, JsonElement item, string speaker, string content, DateTimeOffset? createdAt)
    {
        var mid = TryGetString(item, "mid");
        if (!string.IsNullOrWhiteSpace(mid))
        {
            return $"{cid}|mid:{mid}";
        }

        var sid = TryGetString(item, "sid");
        return BuildMessageKeyForTesting(cid, mid, sid, createdAt, speaker, content);
    }

    private static string TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static bool TryGetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => value.GetString() is "1" or "true" or "True",
            _ => false
        };
    }

    private static DateTimeOffset? TryParseDateTimeOffset(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, out var dto))
        {
            return dto;
        }

        return DateTime.TryParse(value, out var dateTime)
            ? new DateTimeOffset(dateTime)
            : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
