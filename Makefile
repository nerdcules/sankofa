.PHONY: build test test-shell test-shell-daemon test-dotnet bench clean
DAEMON := bin/out/sankofad

build: $(DAEMON)
$(DAEMON): $(shell find daemon -name '*.cs' -o -name '*.csproj' -o -name '*.props' | grep -v /obj/ | grep -v /bin/)
	dotnet publish daemon/sankofad -c Release -r linux-x64 -o bin/out --nologo -v quiet
	@rm -f bin/out/*.pdb bin/out/*.dbg

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
	rm -rf bin/out daemon/*/bin daemon/*/obj tests/0.3/Sankofa.Tests/bin tests/0.3/Sankofa.Tests/obj
