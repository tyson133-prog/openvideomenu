# Open Video Menu menu server (proof of concept): the image published as
# ghcr.io/tyson133-prog/openvideomenu-menu-server. Build it yourself from the repository root:
#   docker build -f docker/asset-server.Dockerfile -t openvideomenu-menu-server .
# Run it next to Plex with host networking (for discovery) and your media read-only, at the same paths
# Plex sees (or set PATH_MAP), with a Plex token so it can read Plex's library folders:
#   docker run -d --name openvideomenu --network host --restart unless-stopped \
#     -v /mnt/media:/mnt/media:ro -e PLEX_URL=http://127.0.0.1:32400 -e PLEX_TOKEN=… \
#     ghcr.io/tyson133-prog/openvideomenu-menu-server

# Build: the server and its dependencies bundled into one file (packaging/bundle.mjs).
FROM node:24-alpine AS build
WORKDIR /src
COPY package.json package-lock.json tsconfig.base.json ./
COPY packages/core/package.json packages/core/
COPY packages/asset-server/package.json packages/asset-server/
COPY packages/renderer-web/package.json packages/renderer-web/
COPY packages/cli/package.json packages/cli/
RUN npm ci --ignore-scripts
COPY packages/core packages/core
COPY packages/asset-server packages/asset-server
COPY packaging/bundle.mjs packaging/
# The bundle reads @openvideomenu/core from its build output.
RUN npx tsc -b packages/core && node packaging/bundle.mjs

# Run: Node and that one file, nothing else.
FROM node:24-alpine
WORKDIR /app
ENV NODE_ENV=production PORT=8790
COPY --from=build /src/packaging/build/menu-server.mjs ./menu-server.mjs
COPY LICENSE NOTICE ./
EXPOSE 8790
# Runs as the unprivileged "node" user; mount the media read-only (:ro).
USER node
ENTRYPOINT ["node", "/app/menu-server.mjs"]
