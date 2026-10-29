package tailnet

import "regexp"

// loginPage matches the pages that log a node in (« https://login.tailscale.com/a/1a2b3c », a test control's
// « …/auth/… »): whoever opens one while it is valid can add the node to their own tailnet.
var loginPage = regexp.MustCompile(`https?://[^\s"'<>]+/(?:a|auth)/[0-9A-Za-z_-]+`)

// credentials matches a user and password written in an address (« http://user:secret@proxy:3128 »), as Tailscale
// logs the system's proxy.
var credentials = regexp.MustCompile(`(?i)\b([a-z][a-z0-9+.-]*://)[^/\s"'<>@]+@`)

// Redact hides the login pages and the credentials written in addresses in a log line.
func Redact(line string) string {
	line = loginPage.ReplaceAllString(line, "<login page>")
	return credentials.ReplaceAllString(line, "${1}<credentials>@")
}
