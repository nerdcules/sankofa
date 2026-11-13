.PHONY: build test test-shell test-shell-daemon test-dotnet dist gui-dist bench clean
# the version stamped into a release (make dist VERSION=1.2.3); default: the CLI's own
VERSION ?= $(shell sed -n 's/^VERSION=//p' bin/sankofa)
# the machine this is built on, x64 or arm64; the .NET runtime id follows
ARCH ?= $(shell uname -m | sed 's/x86_64/x64/; s/aarch64/arm64/')
RID ?= linux-$(ARCH)
DISTDIR ?= dist
DAEMON := bin/out/sankofad

MCP := bin/out/sankofa-mcp
HUB := bin/out/sankofa-hub
TUI := bin/out/sankofa-tui
GUI := bin/out/sankofa-gui
SOURCES := $(shell find daemon -name '*.cs' -o -name '*.csproj' -o -name '*.props' | grep -v /obj/ | grep -v /bin/)

build: $(DAEMON) $(MCP) $(HUB) $(TUI) $(GUI)
$(DAEMON): $(SOURCES)
	dotnet publish daemon/sankofad -c Release -r $(RID) -o bin/out --nologo -v quiet
	@rm -f bin/out/*.pdb bin/out/*.dbg
$(MCP): $(SOURCES)
	dotnet publish daemon/sankofa-mcp -c Release -r $(RID) -o bin/out --nologo -v quiet
	@rm -f bin/out/*.pdb bin/out/*.dbg
$(HUB): $(SOURCES)
	dotnet publish daemon/sankofa-hub -c Release -r $(RID) -o bin/out --nologo -v quiet
	@rm -f bin/out/*.pdb bin/out/*.dbg
# Terminal.Gui ships an Oniguruma wrapper for syntax colouring, which the dashboard never uses
$(TUI): $(SOURCES)
	dotnet publish daemon/sankofa-tui -c Release -r $(RID) -o bin/out --nologo -v quiet
	@rm -f bin/out/*.pdb bin/out/*.dbg bin/out/libonigwrap.so
# the window (Avalonia): Native AOT here, beside its two native libraries (Skia, HarfBuzz)
$(GUI): $(SOURCES)
	dotnet publish daemon/sankofa-gui -c Release -r $(RID) -o bin/out --nologo -v quiet
	@rm -f bin/out/*.pdb bin/out/*.dbg

# a release for this machine's Linux: a tarball that installs from wherever it is unpacked
# (install.sh is inside), and a .deb with the same files under /usr (packaging/deb/build.sh)
DIST := $(DISTDIR)/sankofa-$(VERSION)-linux-$(ARCH)
dist: build
	rm -rf $(DIST) && mkdir -p $(DIST)/bin/out $(DIST)/packaging
	cp bin/sankofa bin/sankofa-watch $(DIST)/bin/
	sed -i 's/^VERSION=.*/VERSION=$(VERSION)/' $(DIST)/bin/sankofa
	cp bin/out/sankofad bin/out/sankofa-mcp bin/out/sankofa-hub bin/out/sankofa-tui $(DIST)/bin/out/
	cp bin/out/sankofa-gui bin/out/libSkiaSharp.so bin/out/libHarfBuzzSharp.so $(DIST)/bin/out/
	cp -r hooks systemd docs $(DIST)/ && cp -r packaging/linux $(DIST)/packaging/
	cp install.sh get.sh AGENTS.md LICENSE README.md $(DIST)/
	tar -C $(DISTDIR) --owner=0 --group=0 -czf $(DISTDIR)/sankofa-linux-$(ARCH).tar.gz sankofa-$(VERSION)-linux-$(ARCH)
	sh packaging/deb/build.sh $(DIST) $(VERSION) $(ARCH) $(DISTDIR)
	@ls -1 $(DISTDIR)/sankofa-linux-$(ARCH).tar.gz $(DISTDIR)/sankofa_$(VERSION)_*.deb

# the window for the other platforms, built from here: one self-contained file each, zipped
GUI_RIDS := win-x64 win-arm64 osx-arm64 osx-x64 linux-arm64
gui-dist:
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

test-shell:
	@for t in tests/*/test_*.sh; do [ -e "$$t" ] || continue; echo "== $$t"; sh "$$t" || exit 1; done

test-shell-daemon:
	@for t in tests/0.1/test_*.sh tests/0.2/test_*.sh; do echo "== $$t (through sankofad)"; SANKOFA_TEST_DAEMON=1 sh "$$t" || exit 1; done

test-dotnet:
	dotnet test tests/0.3/Sankofa.Tests --nologo -v quiet

bench: build
	$(DAEMON) bench

clean:
	rm -rf bin/out dist daemon/*/bin daemon/*/obj tests/0.3/Sankofa.Tests/bin tests/0.3/Sankofa.Tests/obj
