using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SQCD_8005AGV_Simulator.Core.Configuration;

namespace SQCD_8005AGV_Simulator.Core.Services;

public sealed class ModbusTcpServer : IAsyncDisposable
{
    private readonly SimulatorEngine _engine;
    private readonly ModbusSettings _settings;
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptTask;
    private int _nextClientId;

    public ModbusTcpServer(SimulatorEngine engine)
    {
        _engine = engine;
        _settings = engine.Settings.Modbus;
    }

    public bool IsRunning => _listener is not null;
    public int ClientCount => _clients.Count;
    public bool IgnoreRequests { get; set; }
    public bool LogReadRequests { get; set; }

    public event EventHandler<string>? LogEmitted;
    public event EventHandler? ConnectionStateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
            return Task.CompletedTask;

        if (!IPAddress.TryParse(_settings.ListenAddress, out var address))
            throw new InvalidDataException($"无效监听地址：{_settings.ListenAddress}");

        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var listener = new TcpListener(address, _settings.Port);
        try
        {
            listener.Start();
        }
        catch
        {
            listener.Stop();
            lifetime.Dispose();
            throw;
        }

        _lifetime = lifetime;
        _listener = listener;
        _acceptTask = AcceptLoopAsync(lifetime.Token);
        EmitLog($"Modbus TCP 已监听 {_settings.ListenAddress}:{_settings.Port}，Unit ID=0x{_settings.UnitId:X2}。");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        if (listener is null)
            return;

        _listener = null;
        _lifetime?.Cancel();
        listener.Stop();
        DisconnectAllClients();

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _acceptTask = null;
        _lifetime?.Dispose();
        _lifetime = null;
        EmitLog("Modbus TCP 服务已停止。");
    }

    public void DisconnectAllClients()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
        EmitLog("已主动断开全部 Modbus 客户端。");
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                client.NoDelay = true;
                var clientId = Interlocked.Increment(ref _nextClientId);
                _clients[clientId] = client;
                EmitLog($"客户端 #{clientId} 已连接：{client.Client.RemoteEndPoint}");
                ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
                _ = HandleClientAsync(clientId, client, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                EmitLog($"接受客户端失败：{ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(int clientId, TcpClient client, CancellationToken serverToken)
    {
        try
        {
            using var stream = client.GetStream();
            var header = new byte[7];
            while (!serverToken.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, header, serverToken))
                    break;

                var transactionId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
                var protocolId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                var unitId = header[6];

                if (protocolId != 0 || length is < 2 or > 260)
                {
                    EmitLog($"客户端 #{clientId}：非法 MBAP，Protocol={protocolId}，Length={length}。");
                    break;
                }

                var pdu = new byte[length - 1];
                if (!await ReadExactAsync(stream, pdu, serverToken))
                    break;

                if (_settings.StrictUnitId && unitId != _settings.UnitId)
                {
                    EmitLog($"客户端 #{clientId}：忽略 Unit ID 0x{unitId:X2} 请求，期望 0x{_settings.UnitId:X2}。");
                    continue;
                }

                if (IgnoreRequests)
                {
                    EmitLog($"客户端 #{clientId}：已接收 FC=0x{pdu[0]:X2}，故障模式下不响应。");
                    continue;
                }

                var responsePdu = ProcessPdu(pdu);
                var response = BuildAdu(transactionId, unitId, responsePdu);
                await stream.WriteAsync(response, serverToken);
            }
        }
        catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
        {
        }
        catch (IOException ex)
        {
            EmitLog($"客户端 #{clientId} 通信中断：{ex.Message}");
        }
        catch (SocketException ex)
        {
            EmitLog($"客户端 #{clientId} Socket 异常：{ex.SocketErrorCode}");
        }
        catch (Exception ex)
        {
            EmitLog($"客户端 #{clientId} 处理失败：{ex.Message}");
        }
        finally
        {
            client.Dispose();
            _clients.TryRemove(clientId, out _);
            ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
            EmitLog($"客户端 #{clientId} 已断开。");
        }
    }

    internal byte[] ProcessPdu(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length == 0)
            return [0x80, 0x03];

        var function = pdu[0];
        try
        {
            var response = function switch
            {
                0x01 => ProcessReadBits(pdu, true),
                0x02 => ProcessReadBits(pdu, false),
                0x05 => ProcessWriteSingleCoil(pdu),
                0x0F => ProcessWriteMultipleCoils(pdu),
                _ => BuildException(function, 0x01)
            };
            return response;
        }
        catch (ModbusRequestException ex)
        {
            EmitLog($"FC=0x{function:X2} 返回异常码 0x{ex.ExceptionCode:X2}：{ex.Message}");
            return BuildException(function, ex.ExceptionCode);
        }
        catch (Exception ex)
        {
            EmitLog($"FC=0x{function:X2} 内部处理失败：{ex.Message}");
            return BuildException(function, 0x04);
        }
    }

    private byte[] ProcessReadBits(ReadOnlySpan<byte> pdu, bool readDo)
    {
        EnsureLength(pdu, 5);
        var startAddress = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(1, 2));
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(3, 2));
        if (quantity is < 1 or > 2000)
            throw new ModbusRequestException(0x03, "读取数量非法。");

        var baseAddress = readDo ? _settings.DoPduBaseAddress : _settings.DiPduBaseAddress;
        var channelCount = readDo ? _settings.DoChannelCount : _settings.DiChannelCount;
        if (startAddress < baseAddress || startAddress + quantity > baseAddress + channelCount)
            throw new ModbusRequestException(0x02, "读取地址超出 IO 范围。");

        var startChannel = startAddress - baseAddress;
        var values = readDo
            ? _engine.ReadDoRange(startChannel, quantity)
            : _engine.ReadDiRange(startChannel, quantity);
        var byteCount = (quantity + 7) / 8;
        var response = new byte[2 + byteCount];
        response[0] = pdu[0];
        response[1] = (byte)byteCount;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i])
                response[2 + i / 8] |= (byte)(1 << (i % 8));
        }

        if (LogReadRequests)
            EmitLog($"FC=0x{pdu[0]:X2} 读取 PDU={startAddress} 数量={quantity}。");
        return response;
    }

    private byte[] ProcessWriteSingleCoil(ReadOnlySpan<byte> pdu)
    {
        EnsureLength(pdu, 5);
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(1, 2));
        var rawValue = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(3, 2));
        if (address < _settings.DoPduBaseAddress || address >= _settings.DoPduBaseAddress + _settings.DoChannelCount)
            throw new ModbusRequestException(0x02, "DO 地址超出范围。");
        if (rawValue is not (0xFF00 or 0x0000))
            throw new ModbusRequestException(0x03, "0x05 只接受 0xFF00 或 0x0000。");

        var channel = address - _settings.DoPduBaseAddress;
        EmitLog($"收到 FC05：写 DO{channel}，PDU={address}，值={(rawValue == 0xFF00 ? 1 : 0)}（{(rawValue == 0xFF00 ? "0xFF00" : "0x0000")}）。");
        _engine.WriteDo(channel, rawValue == 0xFF00);
        return pdu.ToArray();
    }

    private byte[] ProcessWriteMultipleCoils(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 6)
            throw new ModbusRequestException(0x03, "0x0F 报文长度不足。");

        var startAddress = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(1, 2));
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(3, 2));
        var byteCount = pdu[5];
        if (quantity is < 1 or > 1968 || byteCount != (quantity + 7) / 8 || pdu.Length != 6 + byteCount)
            throw new ModbusRequestException(0x03, "0x0F 数量或字节数非法。");
        if (startAddress < _settings.DoPduBaseAddress || startAddress + quantity > _settings.DoPduBaseAddress + _settings.DoChannelCount)
            throw new ModbusRequestException(0x02, "批量写地址超出 DO 范围。");

        var values = new bool[quantity];
        for (var i = 0; i < quantity; i++)
            values[i] = (pdu[6 + i / 8] & (1 << (i % 8))) != 0;

        var startChannel = startAddress - _settings.DoPduBaseAddress;
        EmitLog($"收到 FC0F：从 DO{startChannel} 开始批量写入，PDU={startAddress}，数量={quantity}。");
        _engine.WriteDoBatch(startChannel, values);

        var response = new byte[5];
        response[0] = 0x0F;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(1, 2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(3, 2), quantity);
        return response;
    }

    private static void EnsureLength(ReadOnlySpan<byte> pdu, int expected)
    {
        if (pdu.Length != expected)
            throw new ModbusRequestException(0x03, $"PDU 长度应为 {expected}，实际为 {pdu.Length}。");
    }

    private static byte[] BuildException(byte function, byte exceptionCode) => [(byte)(function | 0x80), exceptionCode];

    private static byte[] BuildAdu(ushort transactionId, byte unitId, byte[] pdu)
    {
        var response = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), (ushort)(pdu.Length + 1));
        response[6] = unitId;
        pdu.CopyTo(response.AsSpan(7));
        return response;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken token)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], token);
            if (count == 0)
                return false;
            read += count;
        }
        return true;
    }

    private void EmitLog(string message) => LogEmitted?.Invoke(this, $"{DateTime.Now:HH:mm:ss.fff}  {message}");

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed class ModbusRequestException(byte exceptionCode, string message) : Exception(message)
    {
        public byte ExceptionCode { get; } = exceptionCode;
    }
}
