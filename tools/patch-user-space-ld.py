#!/usr/bin/env python3
"""Remove Mono-specific linker script patterns that require binutils 2.38+.

Strips EXCLUDE_FILE(...) wrappers and archive:member references (*lib*.a:*)
from user-space.ld so it can link with older GNU ld (e.g., GCC 10.3).
"""
import re
import sys

if len(sys.argv) != 2:
    print(f"Usage: {sys.argv[0]} <user-space.ld>", file=sys.stderr)
    sys.exit(1)

with open(sys.argv[1], 'r') as f:
    content = f.read()

# Replace *(EXCLUDE_FILE(...) .section) with *(.section)
content = re.sub(r'\*\(EXCLUDE_FILE\([^)]*\)\s+', '*(', content)

# Remove archive:member patterns like *libmonosgen.a:* (.text .text.*)
content = re.sub(r'^\s*\*lib\w+\.a:\*\s+\([^)]*\)\s*\n', '', content, flags=re.MULTILINE)

# Fix CONSTRUCTORS: must be outside *() glob for GNU ld
# *(... CONSTRUCTORS) -> *(...)\n        CONSTRUCTORS
content = re.sub(r'\*\(([^)]*?)\s+CONSTRUCTORS\)', r'*(\1)\n        CONSTRUCTORS', content)

# Replace C++ style comments (//) with C style comments (/* */)
# GNU ld only supports /* */ comments
content = re.sub(r'//(.*)$', r'/* \1 */', content, flags=re.MULTILINE)

with open(sys.argv[1], 'w') as f:
    f.write(content)
