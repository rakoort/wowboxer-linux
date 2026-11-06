# WoWBoxer for Linux: the Avalonia app (`wowboxer`), the CLI (`wbx`) and the KWin agent script the app loads.
# docs/linux/port-plan.md explains the parts. The nuget lock (nix/deps.json) is refreshed with
# `nix build .#default.fetch-deps && ./result nix/deps.json` on an x86_64-linux machine.
{
  lib,
  buildDotnetModule,
  dotnetCorePackages,
  copyDesktopItems,
  makeDesktopItem,
  fontconfig,
  libGL,
  libice,
  libsm,
  libx11,
  libxcursor,
  libxext,
  libxi,
  libxrandr,
  libxkbcommon,
  wayland,
  umu-launcher,
  xdg-utils,
  xprop,
}:
let
  fs = lib.fileset;
  root = ../.;
in
buildDotnetModule (finalAttrs: {
  pname = "wowboxer";
  version = "0.7.1-linux.1";

  src = fs.toSource {
    inherit root;
    fileset = fs.unions [
      ../Directory.Build.props
      ../global.json
      ../WoWBoxer.Layouts.Linux.slnx
      ../src/WoWBoxer.Layouts.Core
      ../src/WoWBoxer.Layouts.Linux
      ../src/WoWBoxer.Layouts.Linux.App
      ../src/WoWBoxer.Layouts.Linux.Cli
      ../src/WoWBoxer.Layouts.App/Assets
      ../tests/WoWBoxer.Layouts.Core.Tests
      ../tests/WoWBoxer.Layouts.Linux.Tests
      ../kwin/wowboxer-agent
    ];
  };

  dotnet-sdk = dotnetCorePackages.sdk_10_0;
  dotnet-runtime = dotnetCorePackages.runtime_10_0;
  nugetDeps = ./deps.json;

  projectFile = [
    "src/WoWBoxer.Layouts.Linux.App/WoWBoxer.Layouts.Linux.App.csproj"
    "src/WoWBoxer.Layouts.Linux.Cli/WoWBoxer.Layouts.Linux.Cli.csproj"
  ];
  testProjectFile = [
    "tests/WoWBoxer.Layouts.Core.Tests/WoWBoxer.Layouts.Core.Tests.csproj"
    "tests/WoWBoxer.Layouts.Linux.Tests/WoWBoxer.Layouts.Linux.Tests.csproj"
  ];
  doCheck = true;
  # Upstream tests that only hold on Windows (docs/linux/port-plan.md, "Build, test, run").
  disabledTests = [
    "WoWBoxer.Layouts.Core.Tests.GameDetectionTests.Game_from_install_folder"
    "WoWBoxer.Layouts.Core.Tests.ProfileStoreTests.File_names_are_safe"
  ];

  executables = [
    "wowboxer"
    "wbx"
  ];

  env.AVALONIA_TELEMETRY_OPTOUT = "1";

  nativeBuildInputs = [ copyDesktopItems ];

  # dlopen-ed by Avalonia (X11 and Wayland backends) and SkiaSharp.
  runtimeDeps = [
    fontconfig
    libGL
    libice
    libsm
    libx11
    libxcursor
    libxext
    libxi
    libxrandr
    libxkbcommon
    wayland
  ];

  # umu-run starts the clients, xprop renames their windows, xdg-open opens links.
  preInstall = ''
    makeWrapperArgs+=(
      --suffix PATH : ${
        lib.makeBinPath [
          umu-launcher
          xdg-utils
          xprop
        ]
      }
    )
  '';

  desktopItems = [
    (makeDesktopItem {
      name = "com.wowboxer.WoWBoxer";
      exec = "wowboxer";
      icon = "wowboxer";
      desktopName = "WoWBoxer";
      comment = finalAttrs.meta.description;
      categories = [
        "Game"
        "Utility"
      ];
      startupWMClass = "wowboxer";
      # Lets `wbx snap` / the app ask KWin for a picture of a WoW window (read-only).
      extraConfig."X-KDE-DBUS-Restricted-Interfaces" = "org.kde.KWin.ScreenShot2";
    })
  ];

  postInstall = ''
    install -Dm644 src/WoWBoxer.Layouts.App/Assets/app.png $out/share/icons/hicolor/256x256/apps/wowboxer.png
  '';

  meta = {
    description = "Places World of Warcraft windows for manual multiboxing (Linux port of WoWBoxer)";
    homepage = "https://github.com/WoW-Multiboxing/wowboxer-layouts";
    license = lib.licenses.mit;
    mainProgram = "wowboxer";
    platforms = [ "x86_64-linux" ];
  };
})
