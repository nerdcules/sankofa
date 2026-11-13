.PHONY: build test test-shell test-shell-daemon test-dotnet dist dist-windows gui-dist bench clean
# the version stamped into a release (make dist VERSION=1.2.3); default: the one compiled in
VERSION ?= $(shell sed -n 's/.*const string Version = "\([^"]*\)".*/\1/p' daemon/Sankofa.Core/Store.cs)
# the machine this is built on: x64 or arm64, Linux or macOS; the .NET runtime id follows
ARCH ?= $(shell uname -m | sed 's/x86_64/x64/; s/aarch64/arm64/')
OS := $(shell uname -s)
ifeq ($(OS),Darwin)
PLAT ?= osx
PKG ?= macos
else
PLAT ?= linux
PKG ?= linux
endif
RID ?= $(PLAT)-$(ARCH)
DISTDIR ?= dist
# GNU tar and bsdtar (macOS) spell "owned by root" differently
TAROWN := $(shell tar --version 2>/dev/null | grep -q GNU && echo "--owner=0 --group=0" || echo "--uid 0 --gid 0")
EXE := $(if $(findstring win-,$(RID)),.exe,)
DAEMON := bin/out/sankofad$(EXE)

MCP := bin/out/sankofa-mcp$(EXE)
HUB := bin/out/sankofa-hub$(EXE)
TUI := bin/out/sankofa-tui$(EXE)
GUI := bin/out/sankofa-gui$(EXE)
CLI := bin/out/sankofa$(EXE)
SOURCES := $(shell find daemon -name '*.cs' -o -name '*.csproj' -o -name '*.props' | grep -v /obj/ | grep -v /bin/)
# Native AOT needs the host's own toolchain: a Windows build from Linux or macOS is self-contained instead
AOT := $(if $(findstring win-,$(RID)),$(if $(findstring MINGW,$(OS)),,-p:PublishAot=false --self-contained),)
PUBLISH = dotnet publish $(1) -c Release -r $(RID) $(AOT) -o bin/out --nologo -v quiet

build: $(DAEMON) $(MCP) $(HUB) $(TUI) $(GUI) $(CLI)
# the command itself (0.14): one native binary on every platform
$(CLI): $(SOURCES)
	$(call PUBLISH,daemon/sankofa-cli)
	@rm -f bin/out/*.pdb bin/out/*.dbg
$(DAEMON): $(SOURCES)
	$(call PUBLISH,daemon/sankofad)
	@rm -f bin/out/*.pdb bin/out/*.dbg
$(MCP): $(SOURCES)
	$(call PUBLISH,daemon/sankofa-mcp)
	@rm -f bin/out/*.pdb bin/out/*.dbg
$(HUB): $(SOURCES)
	$(call PUBLISH,daemon/sankofa-hub)
	@rm -f bin/out/*.pdb bin/out/*.dbg
# Terminal.Gui ships an Oniguruma wrapper for syntax colouring, which the dashboard never uses
$(TUI): $(SOURCES)
	$(call PUBLISH,daemon/sankofa-tui)
	@rm -f bin/out/*.pdb bin/out/*.dbg bin/out/libonigwrap.so bin/out/libonigwrap.dylib bin/out/onigwrap.dll
# the window (Avalonia): Native AOT here, beside its two native libraries (Skia, HarfBuzz)
$(GUI): $(SOURCES)
	$(call PUBLISH,daemon/sankofa-gui)
	@rm -f bin/out/*.pdb bin/out/*.dbg

# a release for this machine (Linux or macOS): a tarball that installs from wherever it is
# unpacked (install.sh is inside), and on Linux a .deb with the same files under /usr
# (packaging/deb/build.sh). The same folder layout on both, so install.sh is one script.
DIST := $(DISTDIR)/sankofa-$(VERSION)-$(PKG)-$(ARCH)
dist: build
	rm -rf $(DIST) && mkdir -p $(DIST)/bin/out $(DIST)/packaging
	cp bin/sankofa bin/sankofa-watch $(DIST)/bin/
	cp bin/out/sankofa bin/out/sankofad bin/out/sankofa-mcp bin/out/sankofa-hub bin/out/sankofa-tui bin/out/sankofa-gui $(DIST)/bin/out/
	cp bin/out/libSkiaSharp.* bin/out/libHarfBuzzSharp.* $(DIST)/bin/out/
	cp -r hooks systemd docs $(DIST)/
	for d in packaging/linux packaging/launchd; do [ ! -d $$d ] || cp -r $$d $(DIST)/packaging/; done
	cp install.sh get.sh AGENTS.md LICENSE README.md $(DIST)/
	tar -C $(DISTDIR) $(TAROWN) -czf $(DISTDIR)/sankofa-$(PKG)-$(ARCH).tar.gz sankofa-$(VERSION)-$(PKG)-$(ARCH)
	@[ "$(PKG)" != linux ] || sh packaging/deb/build.sh $(DIST) $(VERSION) $(ARCH) $(DISTDIR)
	@ls -1 $(DISTDIR)/sankofa-$(PKG)-$(ARCH).tar.gz $(DISTDIR)/sankofa_$(VERSION)_*.deb 2>/dev/null || true

# the Windows package: every program with its dlls (e_sqlite3.dll among them), the hooks and a
# SETUP.txt, zipped. From Linux or macOS the binaries are self-contained (no Native AOT across
# systems); the release workflow builds them as Native AOT on a Windows runner with the same layout.
#   make dist-windows RID=win-x64      make dist-windows RID=win-arm64
WARCH := $(subst win-,,$(RID))
WDIST := $(DISTDIR)/sankofa-$(VERSION)-windows-$(WARCH)
dist-windows:
	@case "$(RID)" in win-*) ;; *) echo "dist-windows needs RID=win-x64 or RID=win-arm64" >&2; exit 1 ;; esac
	rm -rf bin/out $(WDIST)
	$(MAKE) build RID=$(RID)
	mkdir -p $(WDIST) && cp bin/out/*.exe $(WDIST)/ && (cp bin/out/*.dll $(WDIST)/ 2>/dev/null || true)
	cp -r hooks $(WDIST)/ && cp AGENTS.md LICENSE README.md packaging/SETUP-windows.txt $(WDIST)/ && mv $(WDIST)/SETUP-windows.txt $(WDIST)/SETUP.txt
	(cd $(DISTDIR) && rm -f sankofa-windows-$(WARCH).zip && zip -qr sankofa-windows-$(WARCH).zip sankofa-$(VERSION)-windows-$(WARCH))
	@echo "$(DISTDIR)/sankofa-windows-$(WARCH).zip"

# the window alone, for a platform the full packages do not cover (none today: pass GUI_RIDS=...)
GUI_RIDS ?=
gui-dist:
	@[ -n "$(GUI_RIDS)" ] || { echo "gui-dist: every platform has a full package now; pass GUI_RIDS=<rid> for the window alone"; exit 0; }
	@for rid in $(GUI_RIDS); do \
	  dotnet publish daemon/sankofa-gui -c Release -r $$rid -p:PublishAot=false -p:PublishSingleFile=true \
	    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true --self-contained \
	    -o $(DISTDIR)/sankofa-gui-$$rid --nologo -v quiet || exit 1; \
	  rm -f $(DISTDIR)/sankofa-gui-$$rid/*.pdb; \
	  (cd $(DISTDIR) && rm -f sankofa-gui-$$rid.zip && zip -qr sankofa-gui-$$rid.zip sankofa-gui-$$rid) && echo "$(DISTDIR)/sankofa-gui-$$rid.zip"; \
	done

# shell tests run twice: on the local ledger, then with every write through sankofad
test: build test-shell test-shell-daemon test-dotnet
	@echo "all tests passed"

# KEEP=1 runs every test and fails at the end, so one CI run shows all the failures on a new platform
KEEP ?= 0
test-shell:
	@f=0; for t in tests/*/test_*.sh; do [ -e "$$t" ] || continue; echo "== $$t"; sh "$$t" || { f=1; [ "$(KEEP)" = 1 ] || exit 1; }; done; exit $$f

test-shell-daemon:
	@for t in tests/0.1/test_*.sh tests/0.2/test_*.sh; do echo "== $$t (through sankofad)"; SANKOFA_TEST_DAEMON=1 sh "$$t" || exit 1; done

test-dotnet:
	dotnet test tests/0.3/Sankofa.Tests --nologo -v quiet

bench: build
	$(DAEMON) bench

clean:
	rm -rf bin/out dist daemon/*/bin daemon/*/obj tests/0.3/Sankofa.Tests/bin tests/0.3/Sankofa.Tests/obj
