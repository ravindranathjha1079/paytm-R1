#!/usr/bin/env bash
# Adds a second public IP (DNS label seatres-paytm) to senti-vm's NIC as a secondary IP configuration.
# Idempotent. Touches nothing that already exists; everything it creates is tagged app=seatres.
set -euo pipefail
RG=${RG:-senti-rg}
NIC=${NIC:-senti-vmVMNic}
IP_NAME=${IP_NAME:-seatres-paytm-ip}
IPCONFIG=${IPCONFIG:-seatres-ipconfig}
PRIVATE_IP=${PRIVATE_IP:-10.0.0.10}
DNS_LABEL=${DNS_LABEL:-seatres-paytm}
EXPIRES_ON=${EXPIRES_ON:-$(date -u -d '+10 days' +%Y-%m-%d 2>/dev/null || python -c "import datetime;print((datetime.date.today()+datetime.timedelta(days=10)).isoformat())")}

if ! az network public-ip show -g "$RG" -n "$IP_NAME" >/dev/null 2>&1; then
  az network public-ip create -g "$RG" -n "$IP_NAME" --sku Standard --allocation-method Static \
    --dns-name "$DNS_LABEL" --tags app=seatres expiresOn="$EXPIRES_ON" -o none
fi
if ! az network nic ip-config show -g "$RG" --nic-name "$NIC" -n "$IPCONFIG" >/dev/null 2>&1; then
  az network nic ip-config create -g "$RG" --nic-name "$NIC" -n "$IPCONFIG" \
    --private-ip-address "$PRIVATE_IP" --public-ip-address "$IP_NAME" -o none
fi
az network public-ip show -g "$RG" -n "$IP_NAME" --query "{ip:ipAddress,fqdn:dnsSettings.fqdn,tags:tags}" -o json
