param(
    [Parameter(Mandatory)] [string] $Token,
    [string] $Shell = "pwsh",
    [string] $Cwd = $HOME,
    [int] $Port = 8765
)

$uri = [Uri]"ws://127.0.0.1:$Port/ws?token=$Token"
$ws = [Net.WebSockets.ClientWebSocket]::new()
$ct = [Threading.CancellationToken]::None
$ws.ConnectAsync($uri, $ct).GetAwaiter().GetResult()

function Send-Json([hashtable]$Data) {
    $json = $Data | ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $segment = [ArraySegment[byte]]::new($bytes)
    $ws.SendAsync($segment, [Net.WebSockets.WebSocketMessageType]::Text, $true, $ct).GetAwaiter().GetResult()
}

function Receive-Text {
    $buffer = New-Object byte[] 65536
    $stream = [IO.MemoryStream]::new()
    do {
        $segment = [ArraySegment[byte]]::new($buffer)
        $result = $ws.ReceiveAsync($segment, $ct).GetAwaiter().GetResult()
        if ($result.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) { return $null }
        $stream.Write($buffer, 0, $result.Count)
    } while (-not $result.EndOfMessage)
    [Text.Encoding]::UTF8.GetString($stream.ToArray())
}

Write-Host (Receive-Text)
Send-Json @{ action="create"; requestId="create-1"; shell=$Shell; cwd=$Cwd; cols=120; rows=30 }
$created = Receive-Text | ConvertFrom-Json
$sessionId = $created.session.id
Write-Host "Session: $sessionId"
Send-Json @{ action="write"; sessionId=$sessionId; data="Get-Date`r" }

while ($ws.State -eq [Net.WebSockets.WebSocketState]::Open) {
    $message = Receive-Text
    if ($null -eq $message) { break }
    $obj = $message | ConvertFrom-Json
    if ($obj.type -eq "output" -and $obj.sessionId -eq $sessionId) {
        [Console]::Write($obj.data)
    } else {
        Write-Host $message
    }
}
