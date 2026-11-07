# Third-party notices

CX Audio Output includes the following third-party components and assets.

## .NET 8 / ASP.NET Core

- Project: https://github.com/dotnet/aspnetcore
- License: MIT
- Copyright: .NET Foundation and Contributors

The self-contained Windows build includes runtime files from the .NET 8 runtime and ASP.NET Core shared framework.

## Windows App SDK / WinUI 3

- Project: https://github.com/microsoft/WindowsAppSDK
- License: MIT
- Copyright: Microsoft Corporation

The self-contained Windows build includes the Windows App SDK runtime used by the native WinUI 3 control surface.

## QRCoder

- Project: https://github.com/Shane32/QRCoder
- Version: 1.8.1
- License: MIT
- Copyright: Raffael Herrmann, Shane Krueger and contributors

Used by the native WinUI control surface to render the LAN share QR code.

## qrcode-generator

- Project: https://github.com/kazuhikoarase/qrcode-generator
- Version: 1.4.4
- License: MIT
- Copyright: Kazuhiko Arase

The browser build is vendored at `src/CXAudioOutput/wwwroot/assets/qrcode.js`.
The license text is at `src/CXAudioOutput/wwwroot/assets/qrcode-LICENSE.txt`.

## Space Grotesk

- Project: https://github.com/floriankarsten/space-grotesk
- License: SIL Open Font License 1.1
- Copyright: Florian Karsten

The Latin subset is vendored at `src/CXAudioOutput/wwwroot/assets/fonts/space-grotesk-latin.woff2`.
The license text is at `src/CXAudioOutput/wwwroot/assets/OFL-SpaceGrotesk.txt`.

## Manrope

- Project: https://github.com/sharanda/manrope
- License: SIL Open Font License 1.1
- Copyright: Mikhail Sharanda

The Latin subset is vendored at `src/CXAudioOutput/wwwroot/assets/fonts/manrope-latin.woff2`.
The license text is at `src/CXAudioOutput/wwwroot/assets/OFL-Manrope.txt`.

## System components

This application uses the Windows Core Audio APIs through COM interop. Windows and the Windows audio engine are Microsoft products and are not redistributed with this project.
