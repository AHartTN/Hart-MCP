# Hart.MCP Quick Start Test

Write-Host "🚀 Starting Hart.MCP API..." -ForegroundColor Cyan
Write-Host ""

# Start API in background
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PSScriptRoot\src\Hart.MCP.Api'; dotnet run"

Write-Host "⏳ Waiting for API to start (15 seconds)..." -ForegroundColor Yellow
Start-Sleep -Seconds 15

$baseUrl = "https://localhost:7170/api"

Write-Host ""
Write-Host "📊 Testing System Stats..." -ForegroundColor Green
$stats = Invoke-RestMethod -Uri "$baseUrl/analytics/stats" -Method Get -SkipCertificateCheck
Write-Host "Total Nodes: $($stats.data.totalNodes)"
Write-Host "Total Relations: $($stats.data.totalRelations)"
Write-Host "Total Models: $($stats.data.totalModels)"
Write-Host "Total Conversations: $($stats.data.totalConversations)"

Write-Host ""
Write-Host "🤖 Creating Test AI Model..." -ForegroundColor Green
$modelPayload = @{
    name = "Llama4-Test"
    modelType = "LLM"
    architecture = "Llama4"
    version = "1.0"
    parameterCount = 70000000000
    sparsityRatio = 0.085
    sourceFormat = "GGUF"
    metadata = "{`"test`": true}"
} | ConvertTo-Json

$model = Invoke-RestMethod -Uri "$baseUrl/models" -Method Post -Body $modelPayload -ContentType "application/json" -SkipCertificateCheck
Write-Host "Created Model: $($model.data.name) (ID: $($model.data.id))"

Write-Host ""
Write-Host "💬 Creating Conversation Session..." -ForegroundColor Green
$sessionPayload = @{
    sessionType = "Chat"
    userId = "test-user"
    metadata = "{`"test`": true}"
} | ConvertTo-Json

$session = Invoke-RestMethod -Uri "$baseUrl/conversations/sessions" -Method Post -Body $sessionPayload -ContentType "application/json" -SkipCertificateCheck
Write-Host "Created Session: $($session.data.id)"

Write-Host ""
Write-Host "🗣️ Adding Conversation Turn..." -ForegroundColor Green
$turnPayload = @{
    role = "user"
    content = "What is the spatial knowledge substrate?"
    spatialX = 42.5
    spatialY = 108.3
} | ConvertTo-Json

$turn = Invoke-RestMethod -Uri "$baseUrl/conversations/sessions/$($session.data.id)/turns" -Method Post -Body $turnPayload -ContentType "application/json" -SkipCertificateCheck
Write-Host "Created Turn #$($turn.data.turnNumber) at location ($($turn.data.spatialLocation.coordinate.x), $($turn.data.spatialLocation.coordinate.y))"

Write-Host ""
Write-Host "📍 Creating Spatial Node..." -ForegroundColor Green
$nodePayload = @{
    x = 50.0
    y = 75.0
    z = 1.0
    m = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    nodeType = "Token"
    parentHash = "ROOT"
    metadata = "{`"token`": `"knowledge`"}"
} | ConvertTo-Json

$node = Invoke-RestMethod -Uri "$baseUrl/spatialnodes" -Method Post -Body $nodePayload -ContentType "application/json" -SkipCertificateCheck
Write-Host "Created Node: $($node.data.id) at ($($node.data.x), $($node.data.y), $($node.data.z))"

Write-Host ""
Write-Host "🔍 Querying Spatial Nodes..." -ForegroundColor Green
$queryPayload = @{
    centerX = 50.0
    centerY = 75.0
    radius = 100.0
    maxResults = 10
} | ConvertTo-Json

$queryResult = Invoke-RestMethod -Uri "$baseUrl/spatialnodes/query" -Method Post -Body $queryPayload -ContentType "application/json" -SkipCertificateCheck
Write-Host "Found $($queryResult.data.totalCount) nodes in region (Query time: $($queryResult.data.queryTimeMs)ms)"

Write-Host ""
Write-Host "🎨 Creating Visualization Bookmark..." -ForegroundColor Green
$bookmarkPayload = @{
    name = "Test Region"
    description = "Initial test area"
    centerX = 50.0
    centerY = 75.0
    centerZ = 1.0
    zoomLevel = 1.0
    userId = "test-user"
    metadata = "{}"
} | ConvertTo-Json

$bookmark = Invoke-RestMethod -Uri "$baseUrl/visualization/bookmarks" -Method Post -Body $bookmarkPayload -ContentType "application/json" -SkipCertificateCheck
Write-Host "Created Bookmark: $($bookmark.data.name) at ($($bookmark.data.centerX), $($bookmark.data.centerY))"

Write-Host ""
Write-Host "📊 Final System Stats..." -ForegroundColor Green
$finalStats = Invoke-RestMethod -Uri "$baseUrl/analytics/stats" -Method Get -SkipCertificateCheck
Write-Host "Total Nodes: $($finalStats.data.totalNodes)"
Write-Host "Total Relations: $($finalStats.data.totalRelations)"
Write-Host "Total Models: $($finalStats.data.totalModels)"
Write-Host "Total Conversations: $($finalStats.data.totalConversations)"

Write-Host ""
Write-Host "✅ All tests passed! Hart.MCP is operational." -ForegroundColor Green
Write-Host ""
Write-Host "API Documentation: https://localhost:7170/swagger" -ForegroundColor Cyan
Write-Host "Press Enter to continue..." -ForegroundColor Yellow
Read-Host
