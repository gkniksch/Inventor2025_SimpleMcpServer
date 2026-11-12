# Test script to verify InventorMcpServer over stdio JSON-RPC
$exePath = "bin\Debug\net8.0-windows\InventorMcpServer.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "InventorMcpServer.exe not found at $exePath. Run 'dotnet build' first."
    exit 1
}

Write-Host "Starting InventorMcpServer process..." -ForegroundColor Cyan

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = (Resolve-Path $exePath).Path
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

$proc = [System.Diagnostics.Process]::Start($psi)

# Collect stderr in background
$stderrTask = [System.Threading.Tasks.Task]::Run([Action]{
    while (-not $proc.HasExited) {
        $errLine = $proc.StandardError.ReadLine()
        if ($errLine) {
            Write-Host "[STDERR] $errLine" -ForegroundColor DarkGray
        }
    }
})

function Send-JsonRpc([string]$json) {
    Write-Host ">>> SENDING: $json" -ForegroundColor Yellow
    $proc.StandardInput.WriteLine($json)
    $proc.StandardInput.Flush()
    $response = $proc.StandardOutput.ReadLine()
    Write-Host "<<< RECEIVED: $response" -ForegroundColor Green
    return $response | ConvertFrom-Json
}

# 1. Initialize
$initRequest = @{
    jsonrpc = "2.0"
    id = 1
    method = "initialize"
    params = @{
        protocolVersion = "2024-11-05"
        capabilities = @{}
        clientInfo = @{
            name = "TestClient"
            version = "1.0.0"
        }
    }
} | ConvertTo-Json -Compress

$initResponse = Send-JsonRpc $initRequest

# 2. Initialized Notification
$initializedNotice = @{
    jsonrpc = "2.0"
    method = "notifications/initialized"
} | ConvertTo-Json -Compress
$proc.StandardInput.WriteLine($initializedNotice)
$proc.StandardInput.Flush()

# 3. List Tools
$listToolsRequest = @{
    jsonrpc = "2.0"
    id = 2
    method = "tools/list"
    params = @{}
} | ConvertTo-Json -Compress

$toolsResponse = Send-JsonRpc $listToolsRequest

Write-Host "`nRegistered MCP Tools found: $($toolsResponse.result.tools.Count)" -ForegroundColor Cyan
foreach ($tool in $toolsResponse.result.tools) {
    Write-Host " - Tool: $($tool.name)" -ForegroundColor White
    Write-Host "   Description: $($tool.description)" -ForegroundColor Gray
}

# 4. Call reflect_inventor_api (Enum query)
$reflectEnumReq = @{
    jsonrpc = "2.0"
    id = 3
    method = "tools/call"
    params = @{
        name = "reflect_inventor_api"
        arguments = @{
            typeName = "UnitsTypeEnum"
            memberFilter = "kCentimeter"
        }
    }
} | ConvertTo-Json -Compress

$reflectEnumResp = Send-JsonRpc $reflectEnumReq
Write-Host "Enum Reflection Result: $($reflectEnumResp.result.content[0].text)" -ForegroundColor Magenta

# 5. Call reflect_inventor_api (Method search on PartDocument)
$reflectPartReq = @{
    jsonrpc = "2.0"
    id = 4
    method = "tools/call"
    params = @{
        name = "reflect_inventor_api"
        arguments = @{
            typeName = "PartDocument"
            memberFilter = "Save"
        }
    }
} | ConvertTo-Json -Compress

$reflectPartResp = Send-JsonRpc $reflectPartReq
Write-Host "PartDocument Methods Result: $($reflectPartResp.result.content[0].text)" -ForegroundColor Magenta

# 6. Call get_document_context (When Inventor is offline)
$docContextReq = @{
    jsonrpc = "2.0"
    id = 5
    method = "tools/call"
    params = @{
        name = "get_document_context"
        arguments = @{}
    }
} | ConvertTo-Json -Compress

$docContextResp = Send-JsonRpc $docContextReq
Write-Host "Document Context Result: $($docContextResp.result.content[0].text)" -ForegroundColor Magenta

# 7. Call execute_inventor_csharp (Compiler error verification)
$csharpErrReq = @{
    jsonrpc = "2.0"
    id = 6
    method = "tools/call"
    params = @{
        name = "execute_inventor_csharp"
        arguments = @{
            csharpCode = "int invalidSyntax = ;"
        }
    }
} | ConvertTo-Json -Compress

$csharpErrResp = Send-JsonRpc $csharpErrReq
Write-Host "C# Compiler Error Result: $($csharpErrResp.result.content[0].text)" -ForegroundColor Magenta

# Close process
$proc.StandardInput.Close()
$proc.WaitForExit(3000)
Write-Host "`nMCP Server exited with code: $($proc.ExitCode)" -ForegroundColor Cyan
