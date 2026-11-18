Text files are CRLF on disk, LF in the index (.gitattributes). Scripts write with `newline="\r\n"` or let git normalize; compare text ignoring `\r`.
