#!/bin/sh
# Certificates for the cloud topology (ADR-0017): one CA, the broker's certificate, and one client
# certificate for the Gateway and for each edge. Needs only openssl.
#
#   deploy/cloud/certs.sh ca                      # once: the CA that signs everything below
#   deploy/cloud/certs.sh broker <dns-name> broker  # the broker: the name edges use, and 'broker',
#                                                 # the name the Gateway uses inside the stack
#   deploy/cloud/certs.sh client scada-gateway    # the Gateway
#   deploy/cloud/certs.sh client <edge-id>        # each edge: the name IS its Edge:Id
#
# Files go to $CERT_DIR (default deploy/cloud/certs). The CA's key signs every identity in the
# system: keep ca.key off the broker host, and off every edge.
#
# Certificates expire. An expired broker or Gateway certificate stops every edge's data at once; an
# expired edge certificate stops that edge. Nothing warns first — deploy/cloud/README.md says how
# to check, and `certs.sh expiry` prints every date.
set -eu

# Git Bash on Windows rewrites '/CN=…' into a Windows path unless told not to; file paths it must
# still convert, so only arguments beginning /CN are excluded. Ignored everywhere else.
export MSYS2_ARG_CONV_EXCL='/CN'
unset MSYS_NO_PATHCONV

dir="${CERT_DIR:-$(dirname "$0")/certs}"
days_ca="${CA_DAYS:-3650}"
days="${CERT_DAYS:-825}"
mkdir -p "$dir"

need_ca() {
  [ -f "$dir/ca.crt" ] && [ -f "$dir/ca.key" ] || { echo "no CA in $dir: run '$0 ca' first" >&2; exit 1; }
}

# The name becomes the MQTT username and, for an edge, part of its topic: the same characters
# Edge:Id accepts, and no MQTT wildcard or separator.
check_name() {
  case "$1" in
    ''|*[!A-Za-z0-9._-]*) echo "'$1': use letters, digits, '.', '_' and '-' only" >&2; exit 1 ;;
  esac
  # On the edges' listener a client's id is its certificate's name. An edge named like one of the
  # Gateway's client ids (scada-darbox-<device id>) could take over that session: not issued.
  case "$1" in
    scada-darbox-*) echo "'$1': names beginning scada-darbox- are the Gateway's client ids" >&2; exit 1 ;;
  esac
}

sign() { # name, extensions file
  openssl x509 -req -in "$dir/$1.csr" -CA "$dir/ca.crt" -CAkey "$dir/ca.key" -CAcreateserial \
    -days "$days" -sha256 -extfile "$2" -out "$dir/$1.crt" 2>/dev/null
  rm -f "$dir/$1.csr" "$2"
  # Readable by the service that runs as its own user inside its container (Mosquitto drops to
  # uid 1883, the Gateway runs as the app user); the directory is what to keep private.
  chmod 644 "$dir/$1.key"
  echo "$dir/$1.crt  (expires $(openssl x509 -in "$dir/$1.crt" -noout -enddate | cut -d= -f2))"
}

case "${1:-}" in
  ca)
    [ -f "$dir/ca.key" ] && { echo "$dir/ca.key exists; not replacing a CA everything may already trust" >&2; exit 1; }
    openssl req -x509 -newkey rsa:3072 -nodes -keyout "$dir/ca.key" -out "$dir/ca.crt" \
      -days "$days_ca" -sha256 -subj "/CN=SCADA_DARBOX broker CA" 2>/dev/null
    chmod 600 "$dir/ca.key"
    echo "$dir/ca.crt  (expires $(openssl x509 -in "$dir/ca.crt" -noout -enddate | cut -d= -f2))"
    ;;
  broker)
    need_ca; host="${2:?give the broker DNS names: the one edges connect to, then 'broker'}"
    shift
    names=""
    for name in "$@"; do names="${names:+$names,}DNS:$name"; done
    openssl req -newkey rsa:3072 -nodes -keyout "$dir/broker.key" -out "$dir/broker.csr" -subj "/CN=$host" 2>/dev/null
    ext="$dir/broker.ext"
    printf 'basicConstraints=CA:FALSE\nkeyUsage=digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\nsubjectAltName=%s\n' "$names" > "$ext"
    sign broker "$ext"
    ;;
  client)
    need_ca; name="${2:?give the client name: scada-gateway, or the edge id}"; check_name "$name"
    openssl req -newkey rsa:3072 -nodes -keyout "$dir/$name.key" -out "$dir/$name.csr" -subj "/CN=$name" 2>/dev/null
    ext="$dir/$name.ext"
    printf 'basicConstraints=CA:FALSE\nkeyUsage=digitalSignature,keyEncipherment\nextendedKeyUsage=clientAuth\n' > "$ext"
    sign "$name" "$ext"
    ;;
  expiry)
    for crt in "$dir"/*.crt; do
      printf '%-40s %s\n' "$(basename "$crt")" "$(openssl x509 -in "$crt" -noout -enddate | cut -d= -f2)"
    done
    ;;
  *)
    sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
    ;;
esac
