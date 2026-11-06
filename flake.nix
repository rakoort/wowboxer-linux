{
  description = "WoWBoxer for Linux: places World of Warcraft windows for manual multiboxing on KDE Plasma 6";

  inputs.nixpkgs.url = "github:nixos/nixpkgs/nixos-26.05";

  outputs =
    { self, nixpkgs }:
    let
      system = "x86_64-linux";
      pkgs = nixpkgs.legacyPackages.${system};
    in
    {
      # For a NixOS or home-manager config on its own nixpkgs: pkgs.wowboxer.
      overlays.default = final: prev: {
        wowboxer = final.callPackage ./nix/package.nix { };
      };

      packages.${system} = {
        wowboxer = pkgs.callPackage ./nix/package.nix { };
        default = self.packages.${system}.wowboxer;
      };

      # The KWin agent's tests (the .NET tests run in the package's checkPhase).
      checks.${system}.agent =
        pkgs.runCommand "wowboxer-agent-tests"
          {
            nativeBuildInputs = [ pkgs.bun ];
          }
          ''
            cp -r ${./kwin} kwin
            chmod -R u+w kwin
            HOME=$TMPDIR bun test kwin/tests
            touch $out
          '';
    };
}
