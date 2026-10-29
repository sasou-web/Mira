// Built-in Tailscale for Mira's iPhone app (src/Mira.Tailscale/ios). Each function answers in JSON, a string to free
// with MiraTailscaleFree; any thread may call them, some wait (starting, a sign-in page).
#ifndef MIRA_TAILSCALE_H
#define MIRA_TAILSCALE_H

#pragma clang assume_nonnull begin

char * _Nullable MiraTailscaleStart(const char *config);
char * _Nullable MiraTailscaleStatus(void);
char * _Nullable MiraTailscaleCheck(void);
void MiraTailscaleNetwork(const char *name);
char * _Nullable MiraTailscaleLogin(void);
char * _Nullable MiraTailscaleLogout(void);
char * _Nullable MiraTailscaleStop(void);
void MiraTailscaleFree(char * _Nullable text);

#pragma clang assume_nonnull end

#endif
