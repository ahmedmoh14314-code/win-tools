# SharpCompress

- **What:** `SharpCompress.dll`, package SharpCompress 0.50.7 (commit `b4af8afe6683d019d504954a3b4e4f18b06e2cd6`), net10.0 build.
- **Source:** https://github.com/adamhathcock/sharpcompress
- **Licence:** MIT (`LICENSE.txt`). Parts of it carry their authors' own terms, listed below. None is GPL, AGPL or LGPL.
- **Use:** the archive worker only, to read 7z and RAR archives (ADR-0030). WinTools never writes a RAR archive.

## RAR decoder: the unRAR licence

SharpCompress's RAR decoder comes from the UnRAR source through junrar: `Unpack15.cs` and `Unpack20.cs` say that "the unrar licence applies to all junrar source and binary distributions", and that the source may not be used to re-create the RAR compression algorithm. WinTools only decompresses, so it stays within these terms. The paragraph the licence requires:

> UnRAR source code may be used in any software to handle RAR archives without limitations free of charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate form or as a part of other software is permitted, provided that full text of this paragraph, starting from "UnRAR source code" words, is included in license, or in documentation if license is not available, and in source code comments of resulting package.

All copyrights to RAR and the utility UnRAR are exclusively owned by the author, Alexander Roshal.

## Other parts

| Part | Terms |
|---|---|
| Deflate, from DotNetZip: Copyright (c) 2009 Dino Chiesa and Microsoft Corporation | Microsoft Public License (`ms-pl.txt`); derived from jzlib and zlib, whose notices are in the source |
| BZip2, from Apache Ant: Copyright 2001,2004-2005 The Apache Software Foundation | Apache-2.0 (`apache-2.0.txt`) |
| Deflate64: The .NET Foundation | MIT |
| ADC: Copyright (c) 2016 Natalia Portillo, Claunia.com | MIT |
| Bouncy Castle code: Copyright (c) 2000 - 2011 The Legion Of The Bouncy Castle | MIT |
| 7z, LZMA and PPMd: from managed-lzma, the LZMA SDK and Dmitry Shkarin's PPMd | The LZMA SDK and PPMd are in the public domain |
