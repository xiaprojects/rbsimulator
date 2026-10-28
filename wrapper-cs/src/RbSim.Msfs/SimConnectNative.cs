// This file is part of RB.
//
// Copyright (C) 2026 XIAPROJECTS SRL
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.
//
// This source is part of the project RB: RB Simulator
// (github.com/xiaprojects/rbsimulator).
//
// Community edition will be free for all builders and personal use as defined by the licensing model
// Dual licensing for commercial agreement is available
// Please join Discord community
//
// SimConnectNative.cs: P/Invoke to the user's own SimConnect.dll, found at run time in the protocol §3.1 order.

using System.Reflection;
using System.Runtime.InteropServices;

namespace RbSim.Msfs;

/// <summary>
/// The native SimConnect C API. The DLL is never bundled (redistribution unconfirmed, Q14);
/// it is loaded from the MSFS SDK the user installed (RB-SIM-MSF-010). Names and values
/// follow SimConnect.h; the structure offsets used in <see cref="SimConnectClient"/> assume
/// its pack(1) layout (verify, L1).
/// </summary>
internal static class SimConnectNative
{
    const string Dll = "SimConnect.dll";

    public const uint ObjectIdUser = 0;
    public const uint Unused = 0xFFFFFFFF;
    public const int SOk = 0;

    // SIMCONNECT_RECV_ID
    public const uint RecvNull = 0, RecvException = 1, RecvOpen = 2, RecvQuit = 3, RecvEvent = 4,
        RecvEventFilename = 6, RecvSimobjectData = 8, RecvSimobjectDataBytype = 9;

    // SIMCONNECT_PERIOD
    public const uint PeriodNever = 0, PeriodOnce = 1, PeriodSimFrame = 3, PeriodSecond = 4;

    // SIMCONNECT_DATA_REQUEST_FLAG
    public const uint FlagChanged = 1;

    // SIMCONNECT_SIMOBJECT_TYPE
    public const uint SimobjectTypeAircraft = 2;

    // SIMCONNECT_GROUP_PRIORITY_HIGHEST and SIMCONNECT_EVENT_FLAG_GROUPID_IS_PRIORITY, for TransmitClientEvent
    public const uint GroupPriorityHighest = 1, EventFlagGroupIdIsPriority = 0x10;

    // SIMCONNECT_DATATYPE
    public const uint DatatypeFloat64 = 4, DatatypeString8 = 5, DatatypeString32 = 6,
        DatatypeString64 = 7, DatatypeString256 = 9;

    [DllImport(Dll, CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)]
    public static extern int SimConnect_Open(out IntPtr phSimConnect, string szName, IntPtr hWnd,
        uint userEventWin32, IntPtr hEventHandle, uint configIndex);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_Close(IntPtr hSimConnect);

    [DllImport(Dll, CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)]
    public static extern int SimConnect_AddToDataDefinition(IntPtr hSimConnect, uint defineId, string datumName,
        string? unitsName, uint datumType, float epsilon, uint datumId);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_ClearDataDefinition(IntPtr hSimConnect, uint defineId);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_RequestDataOnSimObject(IntPtr hSimConnect, uint requestId, uint defineId,
        uint objectId, uint period, uint flags, uint origin, uint interval, uint limit);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_RequestDataOnSimObjectType(IntPtr hSimConnect, uint requestId, uint defineId,
        uint radiusMeters, uint type);

    [DllImport(Dll, CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)]
    public static extern int SimConnect_SubscribeToSystemEvent(IntPtr hSimConnect, uint eventId, string systemEventName);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_GetNextDispatch(IntPtr hSimConnect, out IntPtr ppData, out uint pcbData);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_GetLastSentPacketID(IntPtr hSimConnect, out uint pdwSendId);

    [DllImport(Dll, CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)]
    public static extern int SimConnect_MapClientEventToSimEvent(IntPtr hSimConnect, uint eventId, string eventName);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_TransmitClientEvent(IntPtr hSimConnect, uint objectId, uint eventId,
        uint dwData, uint groupId, uint flags);

    static IntPtr _lib;

    /// <summary>The DLL actually loaded, for the status page (RB-SIM-MSF-120).</summary>
    public static string? LoadedPath { get; private set; }

    /// <summary>Discovery order of protocol §3.1: config path, SDK environment variables, default SDK folders.</summary>
    public static IEnumerable<string> Candidates(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        const string rel = @"SimConnect SDK\lib\SimConnect.dll";
        foreach (var env in new[] { "MSFS2024_SDK", "MSFS_SDK" })
        {
            var root = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(root)) yield return Path.Combine(root, rel);
        }
        yield return Path.Combine(@"C:\MSFS 2024 SDK", rel);
        yield return Path.Combine(@"C:\MSFS SDK", rel);
        yield return Path.Combine(AppContext.BaseDirectory, Dll);
    }

    /// <summary>Loads SimConnect.dll once and routes the DllImports to it. Returns false with a reason if none is found.</summary>
    public static bool Load(string? configured, out string error)
    {
        error = "";
        if (_lib != IntPtr.Zero) return true;
        var tried = new List<string>();
        foreach (var path in Candidates(configured))
        {
            tried.Add(path);
            if (!File.Exists(path)) continue;
            if (NativeLibrary.TryLoad(path, out _lib))
            {
                LoadedPath = path;
                NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(),
                    (name, _, _) => name == Dll ? _lib : IntPtr.Zero);
                return true;
            }
        }
        error = "SimConnect.dll not found. Install the MSFS SDK (Developer Mode > Help > SDK Installer), "
              + "set MSFS_SDK / MSFS2024_SDK, or pass --simconnect-dll. Tried: " + string.Join("; ", tried);
        return false;
    }

    // SIMCONNECT_EXCEPTION names from SimConnect.h, for the status page (verify the tail against the SDK header).
    static readonly string[] ExceptionNames =
    [
        "NONE", "ERROR", "SIZE_MISMATCH", "UNRECOGNIZED_ID", "UNOPENED", "VERSION_MISMATCH", "TOO_MANY_GROUPS",
        "NAME_UNRECOGNIZED", "TOO_MANY_EVENT_NAMES", "EVENT_ID_DUPLICATE", "TOO_MANY_MAPS", "TOO_MANY_OBJECTS",
        "TOO_MANY_REQUESTS", "WEATHER_INVALID_PORT", "WEATHER_INVALID_METAR", "WEATHER_UNABLE_TO_GET_OBSERVATION",
        "WEATHER_UNABLE_TO_CREATE_STATION", "WEATHER_UNABLE_TO_REMOVE_STATION", "INVALID_DATA_TYPE",
        "INVALID_DATA_SIZE", "DATA_ERROR", "INVALID_ARRAY", "CREATE_OBJECT_FAILED", "LOAD_FLIGHTPLAN_FAILED",
        "OPERATION_INVALID_FOR_OBJECT_TYPE", "ILLEGAL_OPERATION", "ALREADY_SUBSCRIBED", "INVALID_ENUM",
        "DEFINITION_ERROR", "DUPLICATE_ID", "DATUM_ID", "OUT_OF_BOUNDS",
    ];

    public static string ExceptionName(uint code) =>
        code < ExceptionNames.Length ? ExceptionNames[code] : $"EXCEPTION_{code}";
}
