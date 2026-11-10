.PHONY: test
test:
	@for t in tests/*/*.sh; do [ -e "$$t" ] || continue; echo "== $$t"; sh "$$t" || exit 1; done
	@echo "all tests passed"
