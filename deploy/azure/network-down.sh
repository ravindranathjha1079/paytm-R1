#!/usr/bin/env bash
# Removes exactly what network-up.sh created. Safe to re-run.
set -euo pipefail
RG=${RG:-senti-rg}
NIC=${NIC:-senti-vmVMNic}
IP_NAME=${IP_NAME:-seatres-paytm-ip}
IPCONFIG=${IPCONFIG:-seatres-ipconfig}
if az network nic ip-config show -g "$RG" --nic-name "$NIC" -n "$IPCONFIG" >/dev/null 2>&1; then
  az network nic ip-config delete -g "$RG" --nic-name "$NIC" -n "$IPCONFIG" -o none
fi
if az network public-ip show -g "$RG" -n "$IP_NAME" >/dev/null 2>&1; then
  tag=$(az network public-ip show -g "$RG" -n "$IP_NAME" --query tags.app -o tsv)
  [ "$tag" = "seatres" ] || { echo "refusing to delete $IP_NAME: not tagged app=seatres" >&2; exit 1; }
  az network public-ip delete -g "$RG" -n "$IP_NAME" -o none
fi
echo "seatres network removed"
