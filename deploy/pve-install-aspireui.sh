#!/usr/bin/env bash
# AspireUI — Proxmox helper. Run ON the Proxmox host (as root).
# Creates a Debian VM, installs Docker, and runs the AspireUI container
# (with the Docker socket mounted so its Run/Hosting features work).
#
#   bash pve-install-aspireui.sh
#   IP=192.168.1.50 GW=192.168.1.1 RAM=8192 bash pve-install-aspireui.sh
#
# Why a VM (not LXC): AspireUI launches OTHER containers via the Docker socket
# (hosting). That needs real Docker → a VM is the clean, isolated, deletable home.
set -euo pipefail

VMID="${VMID:-$(pvesh get /cluster/nextid)}"
HOSTNAME_="${HOSTNAME_:-aspireui}"
RAM="${RAM:-6144}"            # MiB
CORES="${CORES:-4}"
DISK="${DISK:-40}"            # GiB  (AspireUI hosts other containers → give it room)
BRIDGE="${BRIDGE:-vmbr0}"
STORAGE="${STORAGE:-local-lvm}"     # where the VM disk lives
IP="${IP:-dhcp}"             # e.g. 192.168.1.50/24  or leave 'dhcp'
GW="${GW:-}"                 # gateway (required if IP is static, e.g. 192.168.1.1)
DNS="${DNS:-1.1.1.1}"
IMG_URL="https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-genericcloud-amd64.qcow2"
IMG="/var/lib/vz/template/iso/debian-12-genericcloud-amd64.qcow2"

command -v qm >/dev/null || { echo "Run this on the Proxmox host (qm not found)."; exit 1; }

# 1) Debian cloud image
[ -f "$IMG" ] || { echo "Downloading Debian cloud image…"; mkdir -p "$(dirname "$IMG")"; curl -fSL "$IMG_URL" -o "$IMG"; }

# 2) SSH key so the host can log into the VM to finish setup
[ -f /root/.ssh/id_ed25519.pub ] || ssh-keygen -t ed25519 -N "" -f /root/.ssh/id_ed25519 >/dev/null

# 3) create + configure the VM
echo "== Creating VM $VMID ($HOSTNAME_) =="
qm create "$VMID" --name "$HOSTNAME_" --memory "$RAM" --cores "$CORES" --cpu host \
  --net0 "virtio,bridge=$BRIDGE" --scsihw virtio-scsi-single --ostype l26 --agent 1
qm importdisk "$VMID" "$IMG" "$STORAGE" >/dev/null
qm set "$VMID" --scsi0 "$STORAGE:vm-$VMID-disk-0" >/dev/null
qm disk resize "$VMID" scsi0 "${DISK}G" >/dev/null
qm set "$VMID" --ide2 "$STORAGE:cloudinit" >/dev/null
qm set "$VMID" --boot order=scsi0 --serial0 socket --vga serial0 >/dev/null
qm set "$VMID" --ciuser root --sshkeys /root/.ssh/id_ed25519.pub >/dev/null
if [ "$IP" = "dhcp" ]; then
  qm set "$VMID" --ipconfig0 ip=dhcp >/dev/null
else
  [ -n "$GW" ] || { echo "Set GW=<gateway> when using a static IP."; exit 1; }
  qm set "$VMID" --ipconfig0 "ip=$IP,gw=$GW" --nameserver "$DNS" >/dev/null
fi
qm start "$VMID"

# 4) find the VM IP (static → known; dhcp → ask the guest agent)
if [ "$IP" != "dhcp" ]; then VMIP="${IP%/*}"; else
  echo "Waiting for guest agent to report an IP (dhcp)…"
  for i in $(seq 1 40); do
    VMIP=$(qm guest cmd "$VMID" network-get-interfaces 2>/dev/null \
      | grep -oE '"ip-address" *: *"192\.[0-9.]+"' | grep -oE '192\.[0-9.]+' | head -1) || true
    [ -n "${VMIP:-}" ] && break; sleep 3
  done
  [ -n "${VMIP:-}" ] || { echo "Couldn't detect the DHCP IP. Set a static IP= instead, then re-run."; exit 1; }
fi
echo "VM IP: $VMIP"

# 5) wait for SSH, then install Docker + run AspireUI
echo "Waiting for SSH…"
for i in $(seq 1 60); do ssh -o StrictHostKeyChecking=accept-new -o ConnectTimeout=3 "root@$VMIP" true 2>/dev/null && break; sleep 3; done

ssh -o StrictHostKeyChecking=no "root@$VMIP" 'bash -s' <<'REMOTE'
set -e
export DEBIAN_FRONTEND=noninteractive
cloud-init status --wait >/dev/null 2>&1 || true
for i in $(seq 1 30); do pgrep -x apt-get >/dev/null || break; sleep 3; done
command -v docker >/dev/null || curl -fsSL https://get.docker.com | sh >/dev/null 2>&1
# ghcr can be flaky over IPv4 — retry the pull a few times before running.
for n in 1 2 3 4 5; do docker pull ghcr.io/fgilde/aspireui:latest && break; echo "image pull retry $n…"; sleep 8; done
docker rm -f aspireui 2>/dev/null || true
docker run -d --name aspireui --restart unless-stopped -p 8080:8080 \
  -v aspireui-data:/data -v /var/run/docker.sock:/var/run/docker.sock \
  ghcr.io/fgilde/aspireui:latest
sleep 8
docker ps --filter name=aspireui --format 'AspireUI: {{.Status}}'
REMOTE

echo
echo "Done. AspireUI:  http://$VMIP:8080"
echo "Put it behind a reverse proxy (e.g. Nginx Proxy Manager) for a domain like hosting.example.com."
echo "Delete everything later:  qm stop $VMID && qm destroy $VMID"
