# Bundled tor for Android

`arm64-v8a/libtor.so` is the tor executable the Android app starts for Tor-only accounts. It is an
ordinary position-independent executable (interpreter `/system/bin/linker64`), named like a library
only so that Android packs it into the APK's `lib/arm64-v8a` folder and extracts it into the app's
native library directory, the one place an app may execute a binary from.

| | |
| --- | --- |
| Source | Maven Central, `org.briarproject:tor-android:0.4.9.12` (published 2026-09-10) |
| Built by | the Briar project's reproducible tor build for Android |
| Jar SHA-256 | `81eced0fac386e947c6c75100ee9209dee32c5d98ac2e6c2d2ea99e1854d1dce` (matches Maven Central's `.sha256`) |
| `libtor.so` SHA-256 | `bf36260486d44c2745fda8e4448668a1ee758db0aa79156299979916b03124a3` |
| License | Tor is distributed under the 3-clause BSD license; see https://gitweb.torproject.org/tor.git/tree/LICENSE |

To update it, take `arm64-v8a/libtor.so` out of a newer `tor-android` jar, replace the file here and
update the table.
