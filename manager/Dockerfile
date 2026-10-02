# VibeSkua Manager: a web page to monitor and control a VibeSkua instance.
# No dependencies to install: Node's own modules only. See manager/README.md.
#
#   docker build -t vibeskua-manager manager/
FROM node:24-alpine

WORKDIR /app
COPY package.json server.js ./
COPY public/ public/

ENV NODE_ENV=production PORT=3040
USER node
EXPOSE 3040

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
  CMD wget -qO- http://127.0.0.1:3040/ >/dev/null || exit 1

CMD ["node", "server.js"]
