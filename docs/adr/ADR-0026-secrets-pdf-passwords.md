# ADR-0026: A password goes from the dialogs process to one job and reaches qpdf only on its stdin

- **Status:** Proposed (V2 PR 2.2)
- **Date:** 2026-10-25
- **Deciders:** product owner (2026-10-25): Remove protection never removes an author's restrictions; Protect sets an open password only
- **Related requirements:** V2 plan §9 (PDF password protect, remove protection with the correct password), §14 (passwords never logged, never on a command line, never persisted); ADR-0019 (dialogs process), ADR-0022 (qpdf launch)

## Context
- PDF Protect and Remove protection need a password at run time. The threat model says it must never be logged, on a command line, or stored.
- Facts measured on qpdf 12.4.2 (`docs/research/2026-10-25-qpdf-password-stdin.md`, plan PR 2.2):
  - `@-` reads every argument from stdin, one per line; the encrypt flags accept any text;
  - `--password=X --requires-password` silently exits 0 for a wrong password and 3 for a right one;
  - a restriction-only PDF exits 3 with no password;
  - qpdf decrypts with the open password alone and drops the author's restrictions;
  - its JSON of the encryption holds no password for AES-256.
- **qpdf does not normalize Unicode passwords.** Its manual, "Unicode Passwords", says the standard asks the writer to do it. The product runs with `InvariantGlobalization`, where .NET's `Normalize` leaves non-ASCII text unchanged.
- Whether the dialogs process gets the keyboard after a drop was ADR-0019's open question; the spike is positive (`docs/research/2026-10-25-dialog-focus-after-drop.md`).

## Decision
1. **A command declares a `SecretPrompt`** (title; confirm a new password or not). The host asks `WinTools.Dialogs` for the secret after the drop, before it queues the job.
   - The request carries no path.
   - A cancel runs nothing and says nothing.
   - A missing dialog program says so.
   - The log records only how the dialog ended (`secret.ask`).
2. **One policy (`PasswordPolicy`, Core), applied by the dialog, the host and the worker.**
   - **Always:** not empty, no control character. One argument per line goes on `@-`, and 0x1A ends that input.
   - **A new password (Protect) is used exactly as typed.** It must equal its own NFKC form; "x²" is refused, not rewritten. So a reader that applies SASLprep and one that does not derive the same key.
     - SASLprep's prohibitions are refused, judged by scalar value: invisible formatting, separators, private use, unassigned code points, non-characters.
     - So are the characters SASLprep maps to nothing, such as variation selectors.
     - At most 127 UTF-8 bytes.
   - **An existing password (Remove protection) is tried as typed, then in its NFKC form when that differs.** It came from elsewhere, so it has no length rule and no SASLprep rule.
   - NFKC comes from Windows' `NormalizeString` (`WinTools.Windows.UnicodeText`). The worker, which cannot call it, re-checks the rest.
3. **Only that job gets it.**
   - It travels in its own `Parameters["password"]`, on top of the module's settings, and the scheduler drops it when the job ends.
   - A prompt command without a password, or any other command with parameters of its own, is refused.
   - It reaches the worker on the pipe, and qpdf on its stdin through `@-`: qpdf's command line is `@-` alone.
4. **Protect:** AES-256, the user's password to open it, a random owner password kept nowhere, no restrictions.
   - An encrypted PDF is left alone.
   - The copy must need a password, open with this one, and keep every page.
5. **Remove protection:**
   - only a PDF that needs a password to open, and only with a password that opens it;
   - **never one with any restriction, whatever password is given, the owner's included** (the owner's decision);
   - a restriction-only PDF is refused;
   - a wrong password is `PasswordIncorrect`;
   - **a file qpdf reads only by repairing it is damage, never "not protected"**: qpdf's exit 2 means "not encrypted" or "an error";
   - the copy must not be encrypted and must keep every page.
   - Protect, too, accepts only a file read without a warning: a protected PDF cut short reads as plain.
6. **The dialogs process keeps .NET globalization** (`InvariantGlobalization=false`). WPF's caret asks for the input language's culture, and invariant mode crashed it on an Arabic layout (found by the Explorer run). The host and workers stay invariant.

## Alternatives considered
- **`--password-file=-`** (ADR-0022's first idea). It covers opening a file only; encrypting needs the passwords as arguments, so `@-` covers both.
- **A temporary file holding the password.** It would be written to disk; refused.
- **An environment variable.** Readable by other processes of the user, and inherited by children; refused.
- **Turning off `InvariantGlobalization` for the whole product** to get .NET's normalizer. It changes every culture-sensitive operation in the host and the workers for one call; refused in favour of Windows' own API.
- **Removing restrictions with the owner password from a restriction-only PDF.** The owner chose not to (no bypass of an author's restrictions, whatever password is given).
- **Restriction options and a separate owner password in Protect.** The owner chose to defer them.

## Consequences
- A password lives in the dialogs process, the host and the worker for as long as one job needs it. .NET strings cannot be reliably zeroed; this residual is recorded in the threat model. Any process running as the same user can read another's memory anyway.
- **Every future secret** (encrypted ZIP) reuses the prompt, the policy and the channel.
- A password is refused if it contains a tab, a direction mark or a zero-width character, or is over 127 bytes. The dialog says so at once.
- **Unverified:** SASLprep's bidi rule (a password mixing right-to-left letters with other letters, or one ending in digits after Arabic) is not enforced. Readers that apply strict SASLprep may refuse such a password. This needs one manual check in Acrobat.
- **Settings:** `WinTools.Settings` has editable text boxes and still runs invariant. Whether it crashes the same way is not yet shown (open question).

## Evidence / experiments
- **Core** `PdfPasswordTests`: the policy, its boundaries, the menu, names, the wording.
- **Worker** `PdfPasswordWorkerTests`, 14 tests on the real qpdf:
  - Protect then Remove round-trips the pages;
  - AES-256 with no restriction;
  - a wrong password writes nothing;
  - already encrypted;
  - restriction-only, and restrictions with an open password versus the owner password;
  - nothing to remove;
  - the policy before qpdf;
  - parameters only for these commands;
  - the command line `@-` alone.
- **Host** `JobSecretTests`: only its job gets it; never without one; never logged and dropped at the end; Windows' NFKC.
- **Explorer** (the real dialog, typed Unicode input): Protect 3/3, Remove protection 2/2, regressions passed; the password is in no host log.

## Evaluator review
One round (2026-10-25), on the built worker, the real qpdf and samples in RC4-40/128, AES-128 and AES-256. No blocker.
- **Majors, both fixed:**
  - **NFKC rewrote the password.**
    - "x²", full-width letters and similar characters failed against files that other tools had protected as typed.
    - Protect wrote files that open with "x2", not with "x²".
    - Now: a new password must already be normalized, and an existing one is tried both ways.
  - **The owner password, an equal user and owner password, or an empty owner password lifted restrictions.** That is against the owner's "never". Now any restriction is refused.
- **Minors fixed:**
  - qpdf's exit 2 read as "not protected" for damaged files, and a cut protected PDF was protected again;
  - format characters outside the BMP, U+2028/2029, private use and unassigned code points were allowed;
  - the 127-byte rule applied to existing passwords;
  - two password windows could be open at once (now one at a time, with a toast);
  - "Already protected" was said for a restriction-only PDF ("Already encrypted" now).
- **Recorded:**
  - with Windows Error Reporting LocalDumps configured, a crash dump of the worker or the dialog process could hold the password (threat model);
  - the reviewer found no injection through `@-`: only CR and LF split a line, and the policy refuses both.
- Each finding is a test that failed on the old code. The reviewer's own probes now give the expected refusals.

**Focused re-review:** every first-round finding is confirmed closed, with no blocker or major. Three minors:
- **The strict clean-read rule refused real PDFs with stale offsets.** Acrobat and Edge open them silently. Fixed: qpdf's repair is accepted when the bytes hold no `/Encrypt` and `--check` finds warnings only. A cut protected PDF still fails, because its encrypted streams do not decode (`--check` exits 2, measured).
- **New passwords that NFKC reorders or composes are refused,** including shadda then fatha as an Arabic keyboard types them, a decomposed é, NBSP, and ❤️ with its style marker. The refusal stays: it is the only choice safe across readers. The message now names the cause (mark order, a compatibility character, joined emoji, the emoji style marker).
- **"Already encrypted" for an author-encrypted PDF** reads correctly; no change.
