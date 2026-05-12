import urllib.request, json

url = "https://api.github.com/repos/owasp-amass/amass/releases/latest"
req = urllib.request.Request(url, headers={"User-Agent": "python"})
resp = urllib.request.urlopen(req)
data = json.loads(resp.read())

print("tag:", data["tag_name"])
for asset in data["assets"]:
    print(asset["name"], asset["browser_download_url"])