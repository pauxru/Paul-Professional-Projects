param(
    [string]$BaseUrl = "http://localhost:5022"
)

$tokenResponse = Invoke-RestMethod "$BaseUrl/api/v1/auth/token" -Method Post -ContentType "application/json" -Body (@{
    subject = "demo-user"
    scopes = @("search.manage")
    groups = @("support-agent")
} | ConvertTo-Json)
$headers = @{ Authorization = "Bearer $($tokenResponse.accessToken)" }
$index = "demo-products-v1"

try {
    Invoke-RestMethod "$BaseUrl/api/v1/indices" -Method Post -Headers $headers -ContentType "application/json" -Body (@{ name = $index; alias = "demo-products" } | ConvertTo-Json) | Format-List
}
catch {
    Write-Host "Index may already exist; continuing with the demo." -ForegroundColor Yellow
}

$documents = @(
    @{ id = "demo-laptop"; fields = @{ title = "Contoso lightweight laptop"; body = "Portable notebook with all-day battery"; category = "electronics/laptops" }; numericFields = @{ price = 450 }; popularity = 10; isInStock = $true },
    @{ id = "demo-headset"; fields = @{ title = "Contoso wireless headset"; body = "Noise reduction headphones for support teams"; category = "electronics/audio" }; numericFields = @{ price = 125 }; popularity = 7; isInStock = $true }
)
Invoke-RestMethod "$BaseUrl/api/v1/indices/$index/documents/bulk" -Method Post -Headers $headers -ContentType "application/json" -Body ($documents | ConvertTo-Json -Depth 5) | Format-List
Invoke-RestMethod "$BaseUrl/api/v1/indices/$index/refresh" -Method Post -Headers $headers | Format-List

$search = @{
    index = "demo-products"
    query = "title:(laptop OR notebook) AND price:[100 TO 500]"
    mode = "HybridRrf"
    facets = @(@{ name = "category"; field = "category"; kind = "Hierarchical" })
    highlightFields = @("title", "body")
    explain = $true
} | ConvertTo-Json -Depth 6
Invoke-RestMethod "$BaseUrl/api/v1/search" -Method Post -Headers $headers -ContentType "application/json" -Body $search | ConvertTo-Json -Depth 10
Invoke-RestMethod "$BaseUrl/api/v1/analyze" -Method Post -Headers $headers -ContentType "application/json" -Body '{"text":"<b>Café laptops</b>","analyzer":"standard"}' | ConvertTo-Json -Depth 5
