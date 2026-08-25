using SQCD_8005AGV_Simulator.Core.Models;
using SQCD_8005AGV_Simulator.Core.Services;

namespace SQCD_8005AGV_Simulator.AutomationHost;

public static class AutomationApi
{
    private static readonly byte[] OpenApiDocument = LoadOpenApiDocument();

    public static void Map(WebApplication app, SimulatorEngine engine, ModbusTcpServer modbusServer)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (BadHttpRequestException ex)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(CreateError(
                    engine.GetSnapshot(),
                    null,
                    "INVALID_HTTP_REQUEST",
                    ex.Message));
            }
        });
        app.UseStatusCodePages(async statusContext =>
        {
            var response = statusContext.HttpContext.Response;
            var (reasonCode, message) = response.StatusCode switch
            {
                StatusCodes.Status400BadRequest => ("INVALID_HTTP_REQUEST", "HTTP请求体或参数格式无效。"),
                StatusCodes.Status404NotFound => ("PATH_NOT_FOUND", "请求路径不存在。"),
                StatusCodes.Status405MethodNotAllowed => ("METHOD_NOT_ALLOWED", "请求路径不支持该HTTP方法。"),
                _ => ("HTTP_ERROR", $"HTTP请求失败，状态码{response.StatusCode}。")
            };
            await response.WriteAsJsonAsync(CreateError(engine.GetSnapshot(), null, reasonCode, message));
        });

        var group = app.MapGroup("/api/v1");

        group.MapGet("/health", () => Results.Json(CreateHealth(engine, modbusServer)));
        group.MapGet("/snapshot", () => Results.Json(CreateSnapshot(engine.GetSnapshot(), modbusServer)));
        group.MapPost("/reset", (ResetRequest request) =>
            MapCommandResult(engine.ExecuteResetCommand(ToContext(request.RunId, request.CommandId, request.ExpectedRevision)), modbusServer));

        group.MapPut("/slots/{slotNo:int}/cargo", (int slotNo, CargoRequest request) =>
        {
            if (!TryParseCargo(request.State, out var cargoPresent))
                return InvalidRequest(engine, request.CommandId, "INVALID_CARGO_STATE", "state只接受EMPTY或OCCUPIED。");
            return MapCommandResult(
                engine.ExecuteSetCargoCommand(ToContext(request.RunId, request.CommandId, request.ExpectedRevision), slotNo, cargoPresent),
                modbusServer);
        });

        group.MapPost("/slots/{slotNo:int}/close-door", (int slotNo, ResetRequest request) =>
            MapCommandResult(
                engine.ExecuteCloseDoorCommand(ToContext(request.RunId, request.CommandId, request.ExpectedRevision), slotNo),
                modbusServer));

        group.MapPut("/slots/{slotNo:int}/lock-feedback-override", (int slotNo, OverrideRequest request) =>
        {
            if (!TryParseOverride(request.Mode, out var rawValue))
                return InvalidRequest(engine, request.CommandId, "INVALID_OVERRIDE_MODE", "mode只接受AUTO、FIXED_0或FIXED_1。");
            return MapCommandResult(
                engine.ExecuteLockOverrideCommand(ToContext(request.RunId, request.CommandId, request.ExpectedRevision), slotNo, rawValue),
                modbusServer);
        });

        group.MapPut("/slots/{slotNo:int}/light-curtain-override", (int slotNo, OverrideRequest request) =>
        {
            if (!TryParseOverride(request.Mode, out var rawValue))
                return InvalidRequest(engine, request.CommandId, "INVALID_OVERRIDE_MODE", "mode只接受AUTO、FIXED_0或FIXED_1。");
            return MapCommandResult(
                engine.ExecuteLightOverrideCommand(ToContext(request.RunId, request.CommandId, request.ExpectedRevision), slotNo, rawValue),
                modbusServer);
        });

        group.MapPut("/faults/modbus", (ModbusFaultRequest request) =>
        {
            if (!TryParseFaultMode(request.Mode, out var mode))
                return InvalidRequest(engine, request.CommandId, "INVALID_MODBUS_FAULT_MODE", "mode只接受NORMAL、NO_RESPONSE、DISCONNECT或DELAY。");
            var delayMs = request.DelayMs ?? 0;
            return MapCommandResult(
                engine.ExecuteModbusFaultCommand(ToContext(request.RunId, request.CommandId, request.ExpectedRevision), mode, delayMs),
                modbusServer);
        });

        app.MapGet("/openapi/v1.json", () =>
            Results.Bytes(OpenApiDocument, "application/json; charset=utf-8"));
    }

    private static SimulatorCommandContext ToContext(string? runId, string? commandId, long? expectedRevision) =>
        new(runId ?? string.Empty, commandId ?? string.Empty, expectedRevision ?? 0);

    private static IResult MapCommandResult(SimulatorCommandResult result, ModbusTcpServer server)
    {
        if (result.IsSuccess)
        {
            var snapshot = CreateSnapshot(result.Snapshot, server);
            return Results.Json(new WriteResponse(
                snapshot.SchemaVersion,
                snapshot.InstanceId,
                snapshot.RunId,
                snapshot.Revision,
                DateTimeOffset.UtcNow,
                result.CommandId,
                result.Changed,
                result.Replayed,
                result.AppliedRevision,
                snapshot));
        }

        var statusCode = result.ReasonCode switch
        {
            "INVALID_SLOT_NO" => StatusCodes.Status404NotFound,
            "RUN_ID_MISMATCH" or "REVISION_CONFLICT" or "COMMAND_ID_CONFLICT" or "DOOR_NOT_OPEN" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(CreateError(result.Snapshot, result.CommandId, result.ReasonCode!, result.Error!), statusCode: statusCode);
    }

    private static IResult InvalidRequest(SimulatorEngine engine, string? commandId, string reasonCode, string message) =>
        Results.Json(CreateError(engine.GetSnapshot(), commandId, reasonCode, message), statusCode: StatusCodes.Status400BadRequest);

    private static HealthResponse CreateHealth(SimulatorEngine engine, ModbusTcpServer server)
    {
        var snapshot = engine.GetSnapshot();
        var modbus = CreateModbusState(snapshot, server);
        return new HealthResponse(
            snapshot.SchemaVersion,
            snapshot.InstanceId,
            snapshot.RunId,
            snapshot.Revision,
            DateTimeOffset.UtcNow,
            server.IsRunning && snapshot.ModbusFault.Mode == ModbusFaultMode.Normal ? "READY" : "DEGRADED",
            modbus);
    }

    private static SnapshotResponse CreateSnapshot(SimulatorSnapshot snapshot, ModbusTcpServer server)
    {
        var slots = snapshot.Slots.Select(slot =>
        {
            var faults = new List<string>();
            if (slot.LockFeedbackOverride.HasValue)
                faults.Add("LOCK_FEEDBACK_OVERRIDE");
            if (slot.LightCurtainOverride.HasValue)
                faults.Add("LIGHT_CURTAIN_OVERRIDE");
            if (!slot.LockFeedbackPending && slot.DoorOpen == slot.IsLocked)
                faults.Add("LOCK_FEEDBACK_MISMATCH");
            if (!slot.LightCurtainFeedbackPending && slot.CargoPresent != slot.IsObstructed)
                faults.Add("LIGHT_CURTAIN_MISMATCH");

            return new ApiSlotSnapshot(
                slot.DisplayNumber,
                slot.DoorOpen ? "OPEN" : "CLOSED",
                slot.CargoPresent ? "OCCUPIED" : "EMPTY",
                Convert.ToInt32(slot.UnlockDoRaw),
                Convert.ToInt32(slot.LockFeedbackDiRaw),
                Convert.ToInt32(slot.LightCurtainDiRaw),
                slot.LockFeedbackPending,
                slot.LightCurtainFeedbackPending,
                faults);
        }).ToArray();

        var globalFaults = new List<string>();
        if (snapshot.ModbusFault.Mode != ModbusFaultMode.Normal)
            globalFaults.Add($"MODBUS_{snapshot.ModbusFault.Mode.ToString().ToUpperInvariant()}");
        if (snapshot.OpenDoorCount > snapshot.MaxOpenDoors)
            globalFaults.Add("MAX_OPEN_DOORS_EXCEEDED");
        if (slots.Any(slot => slot.Faults.Count > 0))
            globalFaults.Add("SLOT_FEEDBACK_FAULT");

        return new SnapshotResponse(
            snapshot.SchemaVersion,
            snapshot.InstanceId,
            snapshot.RunId,
            snapshot.Revision,
            DateTimeOffset.UtcNow,
            CreateModbusState(snapshot, server),
            snapshot.DoStates.Select(Convert.ToInt32).ToArray(),
            snapshot.DiStates.Select(Convert.ToInt32).ToArray(),
            slots,
            snapshot.OpenDoorCount,
            snapshot.MaxOpenDoors,
            globalFaults);
    }

    private static ApiModbusState CreateModbusState(SimulatorSnapshot snapshot, ModbusTcpServer server) =>
        new(
            server.ListenAddress,
            server.Port,
            server.UnitId,
            server.IsRunning,
            server.ClientCount,
            snapshot.ModbusFault.Mode.ToString().ToUpperInvariant(),
            snapshot.ModbusFault.DelayMs);

    private static ErrorResponse CreateError(
        SimulatorSnapshot snapshot,
        string? commandId,
        string reasonCode,
        string message) =>
        new(
            snapshot.SchemaVersion,
            snapshot.InstanceId,
            snapshot.RunId,
            snapshot.Revision,
            DateTimeOffset.UtcNow,
            commandId,
            reasonCode,
            message);

    private static bool TryParseCargo(string? value, out bool cargoPresent)
    {
        if (string.Equals(value, "OCCUPIED", StringComparison.OrdinalIgnoreCase))
        {
            cargoPresent = true;
            return true;
        }
        if (string.Equals(value, "EMPTY", StringComparison.OrdinalIgnoreCase))
        {
            cargoPresent = false;
            return true;
        }

        cargoPresent = false;
        return false;
    }

    private static bool TryParseOverride(string? value, out bool? rawValue)
    {
        if (string.Equals(value, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            rawValue = null;
            return true;
        }
        if (string.Equals(value, "FIXED_0", StringComparison.OrdinalIgnoreCase))
        {
            rawValue = false;
            return true;
        }
        if (string.Equals(value, "FIXED_1", StringComparison.OrdinalIgnoreCase))
        {
            rawValue = true;
            return true;
        }

        rawValue = null;
        return false;
    }

    private static bool TryParseFaultMode(string? value, out ModbusFaultMode mode)
    {
        var normalized = value?.Replace("_", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse(normalized, true, out mode) && Enum.IsDefined(mode);
    }

    private static byte[] LoadOpenApiDocument()
    {
        var assembly = typeof(AutomationApi).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".openapi.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("无法读取内嵌的 OpenAPI 文档。");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
