# CryReEncoder
This is a HTTP server that accepts files, optionally re-encodes them, and forwards them to target URL. It's effectively a middleman.

I made this for use with ShareX, where I record with lossless quality very quickly, then instead of uploading directly to target URL, it will instead upload to the re-encoder, passing all the necessary data.

```
ShareX --[lossless video]--> CryReEncoder --[encoded video]--> Target URL
```
The CryReEncoder passes over any headers that were supplied, and also passes back response headers from Target URL, making it a passthrough.

## Usage
Simply send a POST request to the `/` endpoint of wherever CryReEncoder is listening:
- Required header: `TargetUrl` (specifies where it should forward file to, for example an imgur address)
- Required content type: **multipart/form-data**, only accepts a **single file**

Any extra headers added, are passed alongside to `TargetUrl`

By default it simply passes the file on without doing anything, for encoding make sure an encoding profile is defined in `config.json` (if no such file exists, run the app and it will generate it)