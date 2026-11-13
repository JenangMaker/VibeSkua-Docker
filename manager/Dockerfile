# VibeSkua Manager: a web page to monitor and control a VibeSkua instance.
# No dependencies to install: Node's own modules only. See manager/README.md.
#
#   docker build -t vibeskua-manager manager/
FROM node:24-alpine

LABEL org.opencontainers.image.title="VibeSkua Manager" \
      org.opencontainers.image.description="Web control panel for VibeSkua. UI design by DeepSeek V4.1 Flash." \
      org.opencontainers.image.licenses="MIT"

WORKDIR /app
COPY package.json server.js ./
COPY public/ public/
# Ruffle for the Preview (AQW's character viewer in the browser): the
# self-hosted web build, pinned, without its source maps. MIT/Apache-2.0.
# A new version also needs its stylesheet's hash in server.js (RUFFLE_STYLES).
ARG RUFFLE_VERSION=0.7.1
RUN cd /tmp && npm pack --silent "@ruffle-rs/ruffle@${RUFFLE_VERSION}" \
 && tar xzf ruffle-rs-ruffle-*.tgz && mkdir -p /app/public/ruffle \
 && cp package/*.js package/*.wasm package/LICENSE_* /app/public/ruffle/ \
 && rm -rf /tmp/package /tmp/ruffle-rs-ruffle-*.tgz
# A syntax error fails the build rather than the container.
RUN node --check server.js && node --check public/app.js

ENV NODE_ENV=production PORT=3040
USER node
EXPOSE 3040

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
  CMD wget -qO- http://127.0.0.1:3040/ >/dev/null || exit 1

CMD ["node", "server.js"]

