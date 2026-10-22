# Minimal stdio MCP server for tests: one tool "make_file" that writes a file and returns text.
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding $false
$stdin = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
$stdout = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
$stdout.AutoFlush = $true

function Reply($id, $result) {
    $stdout.WriteLine((@{ jsonrpc = '2.0'; id = $id; result = $result } | ConvertTo-Json -Depth 10 -Compress))
}

while (($line = $stdin.ReadLine()) -ne $null) {
    if (-not $line.Trim()) { continue }
    $msg = $line | ConvertFrom-Json
    if ($null -eq $msg.id) { continue }
    switch ($msg.method) {
        'initialize' {
            Reply $msg.id @{ protocolVersion = '2025-06-18'; capabilities = @{ tools = @{} }; serverInfo = @{ name = 'fake'; version = '1' } }
        }
        'tools/list' {
            $tool = @{
                name = 'make_file'; description = 'Writes text into a file'
                inputSchema = @{ type = 'object'; properties = @{ path = @{ type = 'string' }; text = @{ type = 'string' } }; required = @('path', 'text') }
            }
            Reply $msg.id @{ tools = @($tool) }
        }
        'tools/call' {
            $p = $msg.params.arguments
            [System.IO.File]::WriteAllText($p.path, $p.text)
            [Console]::Error.WriteLine("wrote $($p.path)")
            Reply $msg.id @{ content = @(@{ type = 'text'; text = "wrote $($p.text.Length) chars to $($p.path)" }) }
        }
        default {
            $stdout.WriteLine((@{ jsonrpc = '2.0'; id = $msg.id; error = @{ code = -32601; message = 'nope' } } | ConvertTo-Json -Compress))
        }
    }
}
