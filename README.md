# InventorMcpServer

A production-grade C# (.NET 8, Windows x64) **Model Context Protocol (MCP)** server that gives AI agents safe, deterministic, runtime access to a running **Autodesk Inventor 2025** instance via `stdio` transport.

---

## Architecture Overview

```
                      ┌──────────────────────────────────────┐
                      │        AI Client (Claude / IDE)      │
                      └──────────────────┬───────────────────┘
                                         │ stdio (JSON-RPC)
                      ┌──────────────────▼───────────────────┐
                      │          InventorMcpServer           │
                      │  (ModelContextProtocol official SDK) │
                      └───────┬──────────────────────┬───────┘
                              │                      │
                              │ Dispatches           │ Direct Query
                              ▼                      ▼
┌─────────────────────────────────────────┐  ┌─────────────────────────┐
│           StaThreadDispatcher           │  │   reflect_inventor_api  │
│  • Dedicated STA Thread & Message Pump  │  │   • Fast API discovery  │
│  • COM Apartment Safety Guaranteed      │  │   • Method signatures   │
└────────────────────┬────────────────────┘  │   • Enums & properties  │
                     │                       └─────────────────────────┘
         ┌───────────┴───────────┐
         ▼                       ▼
┌──────────────────┐   ┌────────────────────────┐
│  EntityRegistry  │   │ InventorSessionManager │
│  • Handle map    │   │ • ROT P/Invoke attach  │
│  • ReferenceKeys │   │ • No headless spawns   │
│  • Auto-clear    │   │ • Auto-reconnect       │
└──────────────────┘   └───────────┬────────────┘
                                   │ COM Interop
                       ┌───────────▼───────────┐
                       │ Autodesk Inventor 2025│
                       │   (Active Instance)   │
                       └───────────────────────┘
```

### Core Components

1. **`StaThreadDispatcher` (`Services/StaThreadDispatcher.cs`)**
   - Autodesk Inventor's COM API requires execution within a Single-Threaded Apartment (STA).
   - Manages a dedicated background thread initialized with `ApartmentState.STA` and an unbounded `BlockingCollection<Action>` message pump.
   - Installs a custom `StaSynchronizationContext` so all `async/await` continuations marshal deterministically back to the STA thread.

2. **`InventorSessionManager` (`Services/InventorSessionManager.cs`)**
   - Connects to an active `Inventor.Application` instance registered in the Windows Running Object Table (ROT) via native Win32 P/Invoke (`CLSIDFromProgID` from `ole32.dll` and `GetActiveObject` from `oleaut32.dll`).
   - **Never silently launches a headless background `Inventor.exe`**. If Inventor is not running, throws a descriptive, actionable exception.
   - Automatically detects COM disconnections (`RPC_E_DISCONNECTED`, `RPC_S_SERVER_UNAVAILABLE`, `CO_E_OBJNOTCONNECTED`) and re-attaches to the ROT.

3. **`EntityRegistry` (`Services/EntityRegistry.cs`)**
   - Thread-safe handle manager mapping opaque string IDs (`face_1`, `cyl_face_2`, `edge_3`, `occ_4`, `ent_5`) to live COM objects and persistent Inventor `ReferenceKey` byte arrays.
   - Detects active document switches and automatically invalidates handles to prevent stale cross-model references.

4. **Protocol Framing & Logging**
   - All server diagnostics and framework logs are redirected strictly to `stderr` (`Console.Error`).
   - `stdout` remains 100% pristine and reserved exclusively for MCP JSON-RPC protocol framing.

---

## MCP Tools Reference

### 1. `get_document_context`
Inspects the currently active document in Inventor 2025.
- **Returns:**
  - `document_type`: `"PartDocument"`, `"AssemblyDocument"`, `"DrawingDocument"`, etc.
  - `display_name`, `full_file_name`, and `is_dirty` state.
  - `units`: Display length and angle units (noting that internal API units are always `cm` and `rad`).
  - `user_parameters`: List of parameters (`name`, `expression`, evaluated `value`, `units`, `comment`).
  - `occurrences`: If Assembly, top-level occurrences (`name`, `suppressed`, `grounded`, `translation` vector in cm) registered in `EntityRegistry`.
  - `solid_bodies` & `features`: If Part, solid bodies (`face_count`, `edge_count`, `bounding_box`) and feature tree entries.

### 2. `reflect_inventor_api`
Enables the AI agent to introspect exact method signatures, properties, and enum definitions from `Autodesk.Inventor.Interop.dll` on demand without context window bloat.
- **Parameters:**
  - `typeName` (*string, required*): Type name or substring (e.g. `"PartDocument"`, `"ExtrudeFeature"`, `"UnitsTypeEnum"`, `"Face"`).
  - `memberFilter` (*string, optional*): Substring filter for method/property names (e.g. `"Save"`, `"Add"`).
  - `includeInherited` (*bool, optional*): Include inherited members/interfaces (defaults to `false`).

### 3. `query_geometry`
Performs semantic geometric queries in the active model, registers matching entities in `EntityRegistry`, and returns short handles for subsequent tool calls.
- **Parameters:**
  - `queryType` (*string, required*):
    - `"planar_faces"`: Planar faces filtered by normal vector, area, and parent body.
    - `"cylindrical_faces"`: Cylindrical faces filtered by radius, axis vector, and area.
    - `"edges"`: Edges filtered by curve type (`line`, `circle`, `arc`), length, or radius.
    - `"occurrences"`: Assembly components filtered by name substring, grounded, or suppressed state.
    - `"attributes"`: Entities carrying specific Inventor `AttributeSets`.
  - `filterJson` (*string, optional*): JSON filter criteria (e.g. `{"normal": [0, 0, 1], "min_area": 5.0, "tolerance": 0.02}`).

### 4. `execute_inventor_csharp`
Compiles and evaluates C# code dynamically inside a transactional context on the dedicated STA thread.
- **Parameters:**
  - `csharpCode` (*string, required*): C# script to execute. Pre-references `Inventor`, `System`, `System.Linq`, `System.Collections.Generic`, and `System.Text.Json`.
  - `wrapInTransaction` (*bool, optional, default `true`*): Automatically wraps execution in `app.TransactionManager.StartTransaction(Doc, ...)`. Aborts on any compilation error, runtime error, or COM exception to avoid half-built features.
  - `transactionDescription` (*string, optional, default `"MCP Agent Action"`*): Label displayed in Inventor's Undo history.
- **Exposed Script Globals:**
  - `App`: `Inventor.Application`
  - `Doc`: `Inventor.Document` (active document)
  - `TG`: `Inventor.TransientGeometry` (`App.TransientGeometry`)
  - `GetEntity(string handle)`: Resolves an opaque handle from `EntityRegistry`.
  - `RegisterEntity(object obj)`: Registers a COM object into `EntityRegistry`.
  - `Log(string message)`: Captures diagnostic output returned in the tool response.

---

## Installation & Build

### Prerequisites
- Windows 10/11 x64
- Autodesk Inventor 2025 installed (`C:\Program Files\Autodesk\Inventor 2025\`)
- .NET 8.0 SDK (or later)

### Build
```powershell
# Restore dependencies and build x64 release
dotnet build -c Release
```
The compiled executable will be located at:
`bin\Release\net8.0-windows\InventorMcpServer.exe`

---

## MCP Client Configuration

### Claude Desktop Configuration
Add the server entry to your Claude Desktop configuration file:
- **Path:** `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "inventor-2025": {
      "command": "C:\\code\\MyGitHub\\Inventor2025_SimpleMcpServer\\bin\\Release\\net8.0-windows\\InventorMcpServer.exe",
      "args": []
    }
  }
}
```

Or run via `dotnet`:
```json
{
  "mcpServers": {
    "inventor-2025": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:\\code\\MyGitHub\\Inventor2025_SimpleMcpServer\\InventorMcpServer.csproj",
        "-c",
        "Release"
      ]
    }
  }
}
```

---

## Verification & Testing

Run the included verification script `test_mcp_stdio.ps1`:

```powershell
powershell -ExecutionPolicy Bypass -File .\test_mcp_stdio.ps1
```

The script verifies:
1. JSON-RPC handshake (`initialize` and `notifications/initialized`).
2. Tool discovery (`tools/list` reporting all 4 tools and schemas).
3. `reflect_inventor_api` returning enum values and method signatures.
4. `get_document_context` returning structured context / Inventor status.
5. `execute_inventor_csharp` syntax analysis and compilation error diagnostics.

---

## Example C# Automation Scripts

When invoking `execute_inventor_csharp`, pass C# scripts like the examples below:

### Example 1: Create an Extruded Cube in a New Part
```csharp
var partDoc = (PartDocument)Doc;
var compDef = partDoc.ComponentDefinition;

// 1. Create a 2D sketch on the XY work plane
var xyPlane = compDef.WorkPlanes[3]; // XY Plane
var sketch = compDef.Sketches.Add(xyPlane);

// 2. Draw a 5cm x 5cm rectangle centered at origin
var lines = sketch.SketchLines.AddAsTwoPointCenteredRectangle(
    TG.CreatePoint2d(0, 0),
    TG.CreatePoint2d(2.5, 2.5)
);

// 3. Create an Extrude feature of 5 cm
var profile = sketch.Profiles.AddForSolid();
var extrudeDef = compDef.Features.ExtrudeFeatures.CreateExtrudeDefinition(
    profile, 
    PartFeatureOperationEnum.kJoinOperation
);
extrudeDef.SetDistanceExtent(5.0, PartFeatureExtentDirectionEnum.kPositiveExtentDirection);

var extrude = compDef.Features.ExtrudeFeatures.Add(extrudeDef);
Log($"Created Extrude: {extrude.Name}");

return RegisterEntity(extrude);
```

### Example 2: Inspect and Modify User Parameters
```csharp
var partDoc = (PartDocument)Doc;
var userParams = partDoc.ComponentDefinition.Parameters.UserParameters;

// Find or add a parameter
UserParameter lengthParam;
try 
{
    lengthParam = userParams["Length"];
    lengthParam.Expression = "150 mm";
    Log("Updated existing 'Length' parameter to 150 mm.");
}
catch 
{
    lengthParam = userParams.AddByExpression("Length", "100 mm", UnitsTypeEnum.kMillimeterLengthUnits);
    Log("Created new 'Length' parameter.");
}

Doc.Update();
return lengthParam.Value;
```

---

## License
MIT License. See [LICENSE](LICENSE) for details.
