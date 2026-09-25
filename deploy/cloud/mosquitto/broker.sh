#!/bin/sh
# Runs Mosquitto as the container's main process, with its log passed through audit.awk:
# per-packet debug lines are dropped from the container log, and refusals and connections are
# appended to /mosquitto/audit/refused.log, on its own volume. Mosquitto logs a refused publish only
# at debug level, which is why the whole log has to be read to find them.
set -eu

mkdir -p /mosquitto/audit /mosquitto/run

# The ACL as Mosquitto wants it: owned by its user and readable by no one else. The mounted copy is
# read-only and belongs to whoever deployed it.
install -o mosquitto -g mosquitto -m 600 /mosquitto/config/acl /mosquitto/run/acl
fifo=/tmp/mosquitto-log
rm -f "$fifo"
mkfifo "$fifo"

awk -v audit=/mosquitto/audit/refused.log -f /mosquitto/config/audit.awk < "$fifo" &

# exec: Mosquitto becomes the process `docker stop` signals, so it shuts down cleanly rather than
# being killed when the timeout runs out.
exec mosquitto -c /mosquitto/config/mosquitto.conf > "$fifo" 2>&1
