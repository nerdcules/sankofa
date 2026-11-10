.PHONY: test
test:
	@for t in tests/*/test_*.sh; do [ -e "$$t" ] || continue; echo "== $$t"; sh "$$t" || exit 1; done
	@echo "all tests passed"
