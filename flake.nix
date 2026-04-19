{
  outputs = inputs:
    inputs.flake-parts.lib.mkFlake {inherit inputs;} ({...}: {
      systems = ["x86_64-linux" "aarch64-linux"];

      perSystem = {
        config,
        system,
        ...
      }: let
        pkgs = inputs.nixpkgs.legacyPackages.${system};
      in {
        packages = {
          jackify-engine = pkgs.callPackage ./nix/package.nix {};
          default = config.packages.jackify-engine;
        };
      };
    });

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-parts.url = "github:hercules-ci/flake-parts";
  };
}
