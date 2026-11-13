.PHONY: build test test-shell test-shell-daemon test-dotnet gui-dist bench clean
DAEMON := bin/out/sankofad

MCP := bin/out/sankofa-mcp
HUB := bin/out/sankofa-hub
TUI := bin/out/sankofa-tui
GUI := bin/out/sankofa-gui
SOURCES := $(shell find daemon -name '*.cs' -o -name '*.csproj' -o -name '*.props' | grep -v /obj/ | grep -v /bin/)

build: $(DAEMON) $(MCP) $(HUB) $(TUI) $(GUI)
$(DAEMON): $(SOURCES)
	dotnet publish daemon/sankofad -c Release -r linux-x64 -o bin/out --nologo -v quiet
	@rm -f bin/out/*.pdb bin/out/*.dbg
$(MCP): $(SOURCES)
	dotnet publish daemon/sankofa-mcp -c Release -r linux-x64 -o bin/out --nologo -v quiet
	@rm -f bin/out/*.pdb bin/out/*.dbg
$(HUB): $(SOURCES)
	dotnet publish daemon/sankofa-hub -c Release -r linux-x64 -o bin/out --nologo -v quiet
	@rm -f bin/out/*.pdb bin/out/*.dbg
# Terminal.Gui ships an Oniguruma wrapper for syntax colouring, which the dashboard never uses
$(TUI): $(SOURCES)
	dotnet publish daemon/sankofa-tui -c Release -r linux-x64 -o bin/out --nologo -v quiet
	@rm -f bin/out/*.pdb bin/out/*.dbg bin/out/libonigwrap.so
# the window (Avalonia): Native AOT here, beside its two native libraries (Skia, HarfBuzz)
$(GUI): $(SOURCES)
	dotnet publish daemon/sankofa-gui -c Release -r linux-x64 -o bin/out --nologo -v quiet
	@rm -f bin/out/*.pdb bin/out/*.dbg

# the window for the other platforms, built from here: one self-contained file each in dist/
GUI_RIDS := win-x64 win-arm64 osx-arm64 osx-x64 linux-arm64
gui-dist:
	@for rid in $(GUI_RIDS); do \
	  dotnet publish daemon/sankofa-gui -c Release -r $$rid -p:PublishAot=false -p:PublishSingleFile=true \
	    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true --self-contained \
	    -o dist/sankofa-gui-$$rid --nologo -v quiet || exit 1; \
	  rm -f dist/sankofa-gui-$$rid/*.pdb; echo "dist/sankofa-gui-$$rid"; \
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
