# Mosquitto's log, filtered (see broker.sh). Input: every line Mosquitto logs, debug included.

function audit_line() {
    print >> audit
    fflush(audit)
}

# A refusal, with the client id and the topic it tried; and every connection, which names the
# certificate behind a client id (u'<name>'). Together they say who tried what.
/ Denied / { audit_line() }
/ New client connected / { audit_line() }

# A connection refused before it had a name: no certificate, or one from another authority.
/ OpenSSL Error / { audit_line() }

# Per-packet traffic: at debug level, one line per packet in each direction. Not for the log.
/ (Received|Sending) (PUBLISH|PUBACK|PUBREC|PUBREL|PUBCOMP|PINGREQ|PINGRESP|SUBSCRIBE|SUBACK|UNSUBSCRIBE|UNSUBACK|CONNECT|CONNACK|DISCONNECT) / { next }

{
    print
    fflush()
}
