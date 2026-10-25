# Spike: qpdf takes its passwords on stdin, never on the command line (for V2 PR 2.2)

**Date:** 2026-10-25. **Engine:** the pinned qpdf 12.4.2 msvc64 build (ADR-0022). **Status:** spike, not product code.

## Question
PDF protect and unprotect (plan PR 2.2) need a password, and the threat model (plan §14) says: never on a command line, never logged, never persisted. Another process of the same user can read a command line. Can qpdf receive the password any other way?

## Source
qpdf's own help (`qpdf --help=usage`, 12.4.2):
- "`@filename` is an argument file; each line is treated as a separate command-line argument";
- "`@-` may be used to read arguments from stdin".

`qpdf --help=encryption` says the flag form `--encrypt --user-password=… --owner-password=… --bits=… --` (since 11.7.0) "allows you to use any text as the password".

## Experiment
The script is in the session scratchpad. Its only command-line argument is `@-`; every other argument came on stdin, one per line, as UTF-8. The input was `tests/samples/pages-3.pdf`, and the password was `سرّ 😀 with spaces "quotes" and --dashes`.

| Step | Result |
|---|---|
| Encrypt: `in.pdf --encrypt --user-password=… --owner-password=… --bits=256 -- enc.pdf` | exit 0 |
| `--requires-password enc.pdf` | exit 0: a password is required |
| Encryption details (with the password, on stdin) | R = 6 (AES-256); "Supplied password is user password" |
| Decrypt with a wrong password | exit 2, no output file |
| Decrypt with the right password | exit 0; the copy is not encrypted (`--requires-password` exits 2) and has 3 pages |

## Consequences for PR 2.2
- **The password goes to qpdf on stdin, never on the command line.** The PDF worker writes the argument lines to qpdf's stdin and closes it. ADR-0022 currently closes stdin unused and forbids an `@` first argument; PR 2.2 must amend it for this one exact form.
- **A password cannot contain a line break**, since the arguments are split by line. The dialog must refuse CR and LF; every other character works, Unicode included.
- **`--show-encryption` prints the user password to stdout.** The worker must never run it, and qpdf's stdout must never reach a log.
- **Still to design:**
  - the host → dialog → worker secret channel (no file, no log, no environment);
  - zeroing where .NET allows;
  - the dialog itself (entered twice, strength note);
  - "wrong password" as its own error code, not "damaged".
