# PR 2.2: PDF protect and remove protection, with the password dialog and the secret channel

**Status:** approved by the owner on 2026-10-25, with these decisions:
- **Remove protection** never removes an author's restrictions. It acts only on a PDF that needs a password to open, and only with that password.
- **Protect** sets an open password only: AES-256, a random owner password that is not kept, and no restrictions.

**Out of scope:** PDF metadata inspect and remove (a later PR 2.2b), restriction options, and encrypted-ZIP extract (it will reuse the dialog and the channel).

## Evidence it rests on
- `docs/research/2026-10-25-qpdf-password-stdin.md`: qpdf takes every argument on stdin through `@-`, and AES-256 works with a Unicode password.
- A further check (2026-10-25): `--password=X --requires-password` silently exits 0 for a wrong password and 3 for a right one, with nothing on stdout or stderr. A restriction-only PDF exits 3 with no password at all.
- `docs/research/2026-10-25-dialog-focus-after-drop.md`: the dialogs process gets the keyboard after an injected drop. **A manual drop is still pending**, and this PR adds typed text to that evidence.

## Design
1. **Menu:** a new PDF category, "Protect", with "Protect…" and "Remove protection…". With it the PDF ring has five categories, so neither command needs "More…".
2. **Prompt:** a capability can declare a secret prompt (title, and whether to confirm). On a drop, the host asks the dialogs process for the secret before it queues the job. A cancel runs nothing and says nothing.
3. **Dialog:**
   - `WinTools.Dialogs` gains a secret window: a `PasswordBox`, plus a second one to confirm when protecting. It has OK and Cancel, and Enter and Esc, with the message shown in the window.
   - It answers one JSON line on stdout and exits.
   - The request carries no file paths, only the title and the drop point.
4. **Secret channel:** dialogs stdout → host memory → this job's `Parameters["password"]` → the worker's stdin pipe → qpdf's stdin through `@-`.
   - The password is never in a file, a log, an environment variable or a command line.
   - Neither the request nor the parameters are logged.
   - Worker messages never contain the password.
   - `--show-encryption` is never run, because it prints the password.
   - .NET strings cannot be reliably zeroed; this residual is recorded in the threat model.
5. **Password policy** (Core, the same in the dialog, the host and the worker):
   - not empty;
   - no CR, LF or NUL (`@-` reads one argument per line);
   - normalized to NFKC (the standard's SASLprep, so other readers accept it);
   - at most 127 UTF-8 bytes after normalization (AES-256 in PDF truncates silently beyond that).
6. **Worker:**
   - **Protect:** an already encrypted PDF is refused ("already protected"). Otherwise it runs `--encrypt --user-password --owner-password=<random 48 hex> --bits=256`, then checks the copy:
     - it needs a password;
     - the password opens it (`--requires-password` exits 3);
     - the page count is the source's.
   - **Remove protection:**
     - a PDF that is not encrypted is "nothing to do";
     - a restriction-only one is refused (the owner's decision);
     - a wrong password gets a new error, `PasswordIncorrect`, never "damaged";
     - otherwise `--decrypt`, and the copy is checked: not encrypted, same page count.
   - Other PDF commands still refuse a password-protected PDF.
7. **Names and belief:**
   - The outputs are `report (protected).pdf` and `report (unprotected).pdf`, never overwriting.
   - The host believes one under the module's name, with a `%PDF-` header and the SHA-256 the worker validated.
8. **ADR:**
   - a new ADR-0026 for the secret channel and the policy;
   - an ADR-0022 amendment for the single `@-` form;
   - threat-model rows.

## Acceptance criteria
- **Core tests:** the policy (empty, line breaks, NUL, NFKC, 127-byte boundary with multi-byte characters); the menu (a "Protect" category with both commands for a PDF, and only for PDFs); output names; feedback text for `PasswordIncorrect`.
- **Worker tests, real qpdf:**
  - protect, then remove with the same password, round-trips the page count;
  - a protected PDF needs a password, and a wrong one is `PasswordIncorrect` with nothing written;
  - an already protected PDF and a restriction-only PDF are refused;
  - an unencrypted PDF is "nothing to do" for Remove;
  - a Unicode password with quotes, spaces and `--` works;
  - a password violating the policy is refused before qpdf runs;
  - the password appears in no result message;
  - the original is unchanged (hash).
- **Host tests:**
  - the job carries the password only in its own request, not in the module's settings parameters;
  - a cancelled or unavailable dialog runs nothing;
  - belief rules for the new outputs.
- **Explorer:** the harness drops a PDF on Protect…, types the password twice into the real dialog (Unicode input), and the protected copy needs that password. Then Remove protection… on a protected PDF with the right password gives an unencrypted copy.
- 0 warnings in Debug and Release; CI green.
- One evaluator round, plus a focused re-review after any blocker or major.
- **Manual (owner):** one drop by hand on Protect…, and the protected file opened in Acrobat with the same password. Acrobat applies SASLprep, so this checks the NFKC choice.
