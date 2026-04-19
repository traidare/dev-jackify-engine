{
  _7zz-rar,
  buildDotnetModule,
  dotnet-runtime_8,
  dotnet-sdk_8,
  git,
  innoextract,
  lib,
  makeWrapper,
  zlib,
}: let
  repoRoot = ../.;

  src = lib.cleanSource repoRoot;
  projectFile = "jackify-engine/jackify-engine.csproj";
  projectFilePath = repoRoot + "/${projectFile}";

  version = let
    projectFileContents = builtins.readFile projectFilePath;
    versionLine = lib.findFirst (lib.hasInfix "<VERSION ") null (lib.splitString "\n" projectFileContents);
    versionMatch =
      if versionLine == null
      then null
      else builtins.match ''[[:space:]]*<VERSION[^>]*>([^<]+)</VERSION>'' versionLine;
  in
    if versionMatch == null
    then throw "Could not parse jackify-engine version from ${projectFile}"
    else builtins.elemAt versionMatch 0;
in
  buildDotnetModule rec {
    pname = "jackify-engine";

    inherit src version projectFile;

    nugetDeps = ./nuget-deps.json;

    nativeBuildInputs = [
      git
      makeWrapper
    ];

    # SQLite.Interop.dll is a Linux ELF native library that depends on zlib.
    # Listing zlib here lets autoPatchelfHook set the correct RPATH on it.
    buildInputs = [zlib];

    dotnet-sdk = dotnet-sdk_8;
    dotnet-runtime = dotnet-runtime_8;

    # Remove global.json to allow using our Nix-provided .NET SDK
    postPatch = ''
      rm global.json
    '';

    # Remove bundled tool binaries — provided via PATH by postFixup
    postInstall = ''
      rm -f \
        "$out/bin/7zz" \
        "$out/bin/innoextract" \
        "$out/lib/$pname/Extractors/linux-x64/7zz" \
        "$out/lib/$pname/Extractors/linux-x64/innoextract"
    '';

    postFixup = ''
      wrapProgram "$out/bin/$pname" \
        --prefix PATH : ${lib.makeBinPath [
        _7zz-rar
        innoextract
      ]}
    '';

    doCheck = false;

    dotnetFlags = [
      "-p:Version=${version}"
      "-p:InformationalVersion=${version}"
      "-p:FileVersion=${version}"
      "-p:AssemblyVersion=${version}"
    ];

    meta = with lib; {
      description = "Jackify Engine - Linux-native Wabbajack fork";
      homepage = "https://github.com/omni-guides/dev-jackify-engine";
      license = licenses.gpl3;
      platforms = platforms.linux;
    };
  }
