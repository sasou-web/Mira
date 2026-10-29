package tailnet

import "regexp"

// loginPage matches the pages that log a node in (« https://login.tailscale.com/a/1a2b3c », a test control's
// « …/auth/… »): whoever opens one while it is valid can add the node to their own tailnet.
var loginPage = regexp.MustCompile(`https?://[^\s"'<>]+/(?:a|auth)/[0-9A-Za-z_-]+`)

// Redact hides the login pages in a log line.
func Redact(line string) string {
	return loginPage.ReplaceAllString(line, "<login page>")
}
