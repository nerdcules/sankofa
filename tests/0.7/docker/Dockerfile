# One image for every box in the two-machine simulation (sim/hub.py --docker): the hub, the
# desktop and the laptop. Arch, like the machines sankofa is used on, so the Native AOT
# binaries built on the host run unchanged. The build context is assembled by sim/hub.py:
# the tracked files plus bin/out (nothing is built here and nothing here is committed).
FROM archlinux:base
RUN pacman -Syu --noconfirm --needed jq git curl nftables libfaketime procps-ng util-linux python \
 && pacman -Scc --noconfirm
RUN useradd -m -u 1000 dev && install -d -o dev -m 700 /run/user/1000
COPY --chown=dev:dev . /opt/sankofa
ENV XDG_RUNTIME_DIR=/run/user/1000 PATH=/home/dev/.local/bin:/opt/sankofa/tests/0.7/docker:$PATH
CMD ["sleep", "infinity"]
