using ARSoft.Tools.Net;
using ARSoft.Tools.Net.Dns;
using Shadowsocks.Enums;
using Shadowsocks.ViewModel;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Shadowsocks.Model;

[Serializable]
public class DnsClient : ViewModelBase
{
    #region private

    private bool _enable;
    private DnsType _dnsType;
    private bool _ipv6First;
    private string _dnsServer;
    private ushort _port;
    private int _timeout;
    private bool _isEDnsEnabled;
    private string _ecsIp;
    private byte _ecsSourceNetmask;
    private byte _ecsScopeNetmask;
    private bool _isTcpEnabled;
    private bool _isUdpEnabled;

    #endregion

    #region public

    public bool Enable
    {
        get => _enable;
        set => SetField(ref _enable, value);
    }

    public DnsType DnsType
    {
        get => _dnsType;
        set => SetField(ref _dnsType, value);
    }

    public bool Ipv6First
    {
        get => _ipv6First;
        set => SetField(ref _ipv6First, value);
    }

    public string DnsServer
    {
        get => IsValidDns(_dnsServer) ? _dnsServer : DefaultDnsServer;
        set
        {
            if (IsValidDns(value))
            {
                SetField(ref _dnsServer, value);
                _ip = null;
            }
        }
    }

    public ushort Port
    {
        get => _port;
        set => SetField(ref _port, value);
    }

    public int Timeout
    {
        get => _timeout;
        set => SetField(ref _timeout, value);
    }

    public bool IsEDnsEnabled
    {
        get => _isEDnsEnabled;
        set => SetField(ref _isEDnsEnabled, value);
    }

    public string EcsIp
    {
        get => IsIp(_ecsIp) ? _ecsIp : DefaultDnsServer;
        set
        {
            if (IsIp(value))
            {
                SetField(ref _ecsIp, value);
            }
        }
    }

    public byte EcsSourceNetmask
    {
        get => _ecsSourceNetmask;
        set => SetField(ref _ecsSourceNetmask, value);
    }

    public byte EcsScopeNetmask
    {
        get => _ecsScopeNetmask;
        set => SetField(ref _ecsScopeNetmask, value);
    }

    public bool IsTcpEnabled
    {
        get => _isTcpEnabled;
        set => SetField(ref _isTcpEnabled, value);
    }

    public bool IsUdpEnabled
    {
        get => _isUdpEnabled;
        set => SetField(ref _isUdpEnabled, value);
    }

    #endregion

    #region Ignore

    private IPAddress? _ip;
    public const string DefaultDnsServer = @"208.67.222.222";
    public const ushort DefaultPort = 53;
    public const string DefaultTlsDnsServer = @"208.67.222.222";
    public const ushort DefaultTlsPort = 853;

    #endregion

    #region 构造函数

    [JsonConstructor]
    public DnsClient()
    {
        _ip = null;

        _enable = true;
        _dnsType = DnsType.Default;
        _ipv6First = false;
        _dnsServer = DefaultDnsServer;
        _port = DefaultPort;
        _timeout = 10000;
        _isEDnsEnabled = false;
        _ecsIp = DefaultDnsServer;
        _ecsSourceNetmask = 32;
        _ecsScopeNetmask = 0;
        _isTcpEnabled = true;
        _isUdpEnabled = true;
    }

    public DnsClient(DnsType type) : this()
    {
        _dnsType = type;
        switch (type)
        {
            case DnsType.Default:
            {
                _dnsServer = DefaultDnsServer;
                _port = DefaultPort;
                break;
            }
            case DnsType.DnsOverTls:
            {
                _dnsServer = DefaultTlsDnsServer;
                _port = DefaultTlsPort;
                break;
            }
        }
    }

    #endregion

    #region Private Method

    private static bool IsIp(string? str)
    {
        return IPAddress.TryParse(str, out var ip) && ip.ToString() == str;
    }

    private bool IsValidDns(string? dns)
    {
        return DnsType == DnsType.DnsOverTls || IsIp(dns);
    }

    private static async Task<IPAddress?> QueryBaseAAsync(IPAddress serverIp, ushort port, int timeout, bool isTcpEnabled, bool isUdpEnabled, DomainName domain, DnsQueryOptions options, CancellationToken ct)
    {
        DnsMessage? message = null;
        try
        {
            message = await QueryPlainAsync(serverIp, port, timeout, isTcpEnabled, isUdpEnabled, domain, RecordType.A, options, ct);
        }
        catch
        {
            // ignored
        }
        return message?.AnswerRecords?.OfType<ARecord>().Select(answerRecord => answerRecord.Address).FirstOrDefault();
    }

    private static async Task<IPAddress?> QueryBaseAaaaAsync(IPAddress serverIp, ushort port, int timeout, bool isTcpEnabled, bool isUdpEnabled, DomainName domain, DnsQueryOptions options, CancellationToken ct)
    {
        DnsMessage? message = null;
        try
        {
            message = await QueryPlainAsync(serverIp, port, timeout, isTcpEnabled, isUdpEnabled, domain, RecordType.Aaaa, options, ct);
        }
        catch
        {
            // ignored
        }
        return message?.AnswerRecords?.OfType<AaaaRecord>().Select(answerRecord => answerRecord.Address).FirstOrDefault();
    }

    private static async Task<IPAddress?> QueryBaseAsync(IPAddress serverIp, ushort port, int timeout, bool isTcpEnabled, bool isUdpEnabled, DomainName domain, DnsQueryOptions options, bool ipv6First, CancellationToken ct)
    {
        var res = await Task.WhenAll(
            QueryBaseAaaaAsync(serverIp, port, timeout, isTcpEnabled, isUdpEnabled, domain, options, ct),
            QueryBaseAAsync(serverIp, port, timeout, isTcpEnabled, isUdpEnabled, domain, options, ct));

        if (ipv6First)
        {
            return res[0] ?? res[1];
        }

        return res[1] ?? res[0];
    }

    private static async Task<DnsMessage?> QueryPlainAsync(IPAddress serverIp, ushort port, int timeout, bool isTcpEnabled, bool isUdpEnabled, DomainName domain, RecordType recordType, DnsQueryOptions options, CancellationToken ct)
    {
        var query = CreateDnsQuery(domain, recordType, RecordClass.INet, options);

        if (isUdpEnabled)
        {
            try
            {
                var udpResponse = await QueryUdpAsync(serverIp, port, timeout, query, ct);
                if (udpResponse is not null && (!udpResponse.IsTruncated || !isTcpEnabled))
                {
                    return udpResponse;
                }
            }
            catch
            {
                // Fallback to TCP when configured.
            }
        }

        return isTcpEnabled ? await QueryTcpAsync(serverIp, port, timeout, query, ct) : null;
    }

    private static async Task<DnsMessage?> QueryUdpAsync(IPAddress serverIp, ushort port, int timeout, byte[] query, CancellationToken ct)
    {
        using var timeoutCts = CreateTimeoutTokenSource(timeout, ct);
        var token = timeoutCts.Token;

        using var udpClient = new UdpClient(serverIp.AddressFamily);
        udpClient.Connect(serverIp, port);
        await udpClient.SendAsync(query, query.Length).WaitAsync(token);

        var result = await udpClient.ReceiveAsync().WaitAsync(token);
        return DnsMessage.Parse(result.Buffer);
    }

    private static async Task<DnsMessage?> QueryTcpAsync(IPAddress serverIp, ushort port, int timeout, byte[] query, CancellationToken ct)
    {
        using var timeoutCts = CreateTimeoutTokenSource(timeout, ct);
        var token = timeoutCts.Token;

        using var tcpClient = new TcpClient(serverIp.AddressFamily);
        await tcpClient.ConnectAsync(serverIp, port, token);

        return await SendLengthPrefixedDnsQueryAsync(tcpClient.GetStream(), query, token);
    }

    private static async Task<IPAddress?> QueryBaseTlsAAsync(IPAddress serverIp, string authName, ushort port, int timeout, DomainName domain, DnsQueryOptions options, CancellationToken ct)
    {
        DnsMessage? message = null;
        try
        {
            message = await QueryTlsAsync(serverIp, authName, port, timeout, domain, RecordType.A, options, ct);
        }
        catch
        {
            // ignored
        }
        return message?.AnswerRecords?.OfType<ARecord>().Select(answerRecord => answerRecord.Address).FirstOrDefault();
    }

    private static async Task<IPAddress?> QueryBaseTlsAaaaAsync(IPAddress serverIp, string authName, ushort port, int timeout, DomainName domain, DnsQueryOptions options, CancellationToken ct)
    {
        DnsMessage? message = null;
        try
        {
            message = await QueryTlsAsync(serverIp, authName, port, timeout, domain, RecordType.Aaaa, options, ct);
        }
        catch
        {
            // ignored
        }
        return message?.AnswerRecords?.OfType<AaaaRecord>().Select(answerRecord => answerRecord.Address).FirstOrDefault();
    }

    private static async Task<IPAddress?> QueryBaseTlsAsync(IPAddress serverIp, string authName, ushort port, int timeout, DomainName domain, DnsQueryOptions options, bool ipv6First, CancellationToken ct)
    {
        if (ipv6First)
        {
            var res = await Task.WhenAll(
                QueryBaseTlsAaaaAsync(serverIp, authName, port, timeout, domain, options, ct),
                QueryBaseTlsAAsync(serverIp, authName, port, timeout, domain, options, ct));
            return res[0] ?? res[1];
        }
        else
        {
            var res = await Task.WhenAll(QueryBaseTlsAAsync(serverIp, authName, port, timeout, domain, options, ct));
            return res[0];
        }
    }

    private static async Task<DnsMessage?> QueryTlsAsync(IPAddress serverIp, string authName, ushort port, int timeout, DomainName domain, RecordType recordType, DnsQueryOptions options, CancellationToken ct)
    {
        using var timeoutCts = CreateTimeoutTokenSource(timeout, ct);
        var token = timeoutCts.Token;

        using var tcpClient = new TcpClient(serverIp.AddressFamily);
        await tcpClient.ConnectAsync(serverIp, port, token);

        await using var sslStream = new SslStream(tcpClient.GetStream(), false);
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = authName,
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12
        }, token);

        var query = CreateDnsQuery(domain, recordType, RecordClass.INet, options);
        return await SendLengthPrefixedDnsQueryAsync(sslStream, query, token);
    }

    private static async Task<DnsMessage?> SendLengthPrefixedDnsQueryAsync(Stream stream, byte[] query, CancellationToken ct)
    {
        var lengthPrefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lengthPrefix, (ushort)query.Length);
        await stream.WriteAsync(lengthPrefix, ct);
        await stream.WriteAsync(query, ct);
        await stream.FlushAsync(ct);

        var responseLengthBuffer = new byte[2];
        await stream.ReadExactlyAsync(responseLengthBuffer, ct);
        var responseLength = BinaryPrimitives.ReadUInt16BigEndian(responseLengthBuffer);
        if (responseLength == 0)
        {
            return null;
        }

        var response = new byte[responseLength];
        await stream.ReadExactlyAsync(response, ct);
        return DnsMessage.Parse(response);
    }

    private static CancellationTokenSource CreateTimeoutTokenSource(int timeout, CancellationToken ct)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > 0)
        {
            timeoutCts.CancelAfter(timeout);
        }
        return timeoutCts;
    }

    private static byte[] CreateDnsQuery(DomainName domain, RecordType recordType, RecordClass recordClass, DnsQueryOptions options)
    {
        using var stream = new MemoryStream();

        WriteUInt16(stream, RandomNumberGenerator.GetInt32(ushort.MaxValue + 1));
        WriteUInt16(stream, options.IsRecursionDesired ? 0x0100 : 0);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, options.IsEDnsEnabled ? 1 : 0);

        WriteDomainName(stream, domain);
        WriteUInt16(stream, (ushort)recordType);
        WriteUInt16(stream, (ushort)recordClass);

        if (options.IsEDnsEnabled)
        {
            WriteOptRecord(stream, options);
        }

        return stream.ToArray();
    }

    private static void WriteDomainName(Stream stream, DomainName domain)
    {
        foreach (var label in domain.Labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length > 63)
            {
                throw new InvalidDataException("DNS label length exceeds 63 bytes.");
            }
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    private static void WriteOptRecord(Stream stream, DnsQueryOptions options)
    {
        var ednsOptions = CreateEdnsOptions(options);

        stream.WriteByte(0);
        WriteUInt16(stream, 41);
        WriteUInt16(stream, options.EDnsOptions?.UdpPayloadSize is > 0 ? options.EDnsOptions.UdpPayloadSize : 4096);
        WriteUInt32(stream, options.IsDnsSecOk ? 0x8000u : 0u);
        WriteUInt16(stream, ednsOptions.Length);
        stream.Write(ednsOptions);
    }

    private static byte[] CreateEdnsOptions(DnsQueryOptions options)
    {
        using var stream = new MemoryStream();

        foreach (var clientSubnet in options.EDnsOptions?.Options.OfType<ClientSubnetOption>() ?? [])
        {
            WriteClientSubnetOption(stream, clientSubnet);
        }

        return stream.ToArray();
    }

    private static void WriteClientSubnetOption(Stream stream, ClientSubnetOption clientSubnet)
    {
        var address = clientSubnet.Address;
        var addressBytes = address.GetAddressBytes();
        var family = address.AddressFamily == AddressFamily.InterNetworkV6 ? 2 : 1;
        var maxPrefix = addressBytes.Length * 8;
        var sourceNetmask = Math.Min(clientSubnet.SourceNetmask, maxPrefix);
        var scopeNetmask = Math.Min(clientSubnet.ScopeNetmask, maxPrefix);
        var significantBytes = (sourceNetmask + 7) / 8;
        var subnetBytes = new byte[significantBytes];
        Array.Copy(addressBytes, subnetBytes, significantBytes);

        var remainingBits = sourceNetmask % 8;
        if (remainingBits != 0 && subnetBytes.Length > 0)
        {
            subnetBytes[^1] &= (byte)(0xff << (8 - remainingBits));
        }

        WriteUInt16(stream, 8);
        WriteUInt16(stream, 4 + subnetBytes.Length);
        WriteUInt16(stream, family);
        stream.WriteByte((byte)sourceNetmask);
        stream.WriteByte((byte)scopeNetmask);
        stream.Write(subnetBytes);
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    #endregion

    public static async Task<IPAddress?> QueryIpAddressDefaultAsync(string host, bool ipv6First, CancellationToken ct)
    {
        IPAddress[] ips = await Dns.GetHostAddressesAsync(host, ct);

        if (ipv6First)
        {
            foreach (var ip in ips)
            {
                if (ip.AddressFamily is AddressFamily.InterNetworkV6)
                {
                    return ip;
                }
            }
        }

        return ips.FirstOrDefault();
    }

    public async Task<IPAddress?> QueryIpAddressAsync(string host, CancellationToken ct)
    {
        var domain = DomainName.Parse(host);
        var options = new DnsQueryOptions
        {
            IsEDnsEnabled = IsEDnsEnabled,
            IsRecursionDesired = true,
        };
        if (options.IsEDnsEnabled)
        {
            options.EDnsOptions = new OptRecord { Options = { new ClientSubnetOption(EcsSourceNetmask, EcsScopeNetmask, IPAddress.Parse(EcsIp)) } };
        }
        switch (DnsType)
        {
            case DnsType.Default:
            {
                return await QueryBaseAsync(IPAddress.Parse(DnsServer), Port, Timeout, IsTcpEnabled, IsUdpEnabled, domain, options, Ipv6First, ct);
            }
            case DnsType.DnsOverTls:
            {
                _ip ??= await QueryIpAddressDefaultAsync(DnsServer, Ipv6First, ct);
                if (_ip is null)
                {
                    return null;
                }
                var res = await QueryBaseTlsAsync(_ip, DnsServer, Port, Timeout, domain, options, Ipv6First, ct);
                if (res is null)
                {
                    _ip = null;
                }
                return res;
            }
            default:
                return null;
        }
    }

}
