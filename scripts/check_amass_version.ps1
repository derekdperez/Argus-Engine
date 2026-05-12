$uri='https://api.github.com/repos/owasp-amass/amass/releases/latest'
$resp = Invoke-RestMethod -Uri $uri -UseBasicParsing
Write-Host ('tag: ' + $resp.tag_name)
foreach ($a in $resp.assets) {
    Write-Host ($a.name + ' ' + $a.browser_download_url)
}