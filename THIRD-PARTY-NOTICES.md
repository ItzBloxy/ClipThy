# Third-party notices

ClipThy is MIT-licensed (see `LICENSE`). It uses the components below.

## Included in the release builds

### NAudio (NAudio.Core, NAudio.Wasapi 2.2.1)

WASAPI loopback and microphone capture.
Licence: MIT. Copyright (c) Mark Heath and contributors.
https://github.com/naudio/NAudio

### .NET runtime and Windows Desktop runtime (self-contained build only)

The self-contained zip bundles the .NET 10 runtime and Windows Forms.
Licence: MIT. Copyright (c) .NET Foundation and Contributors.
https://github.com/dotnet/runtime and https://github.com/dotnet/winforms
Their own third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT

## Required but not included

### FFmpeg

ClipThy runs `ffmpeg` as a separate program to capture and encode the screen and to write the MP4. It is not
bundled. You install it yourself, and it is covered by its own licence (LGPL 2.1 or later, or GPL for builds
that enable GPL components, such as gyan.dev's full build).
https://ffmpeg.org/legal.html

---

The MIT licence text that applies to NAudio and .NET (with their own copyright lines above):

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
