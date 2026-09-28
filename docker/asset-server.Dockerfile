# Open Video Menu asset server (proof of concept).
# Build from the repository root:
#   docker build -f docker/asset-server.Dockerfile -t openvideomenu-asset-server .
# Run next to Plex, with media mounted READ-ONLY and host networking (for discovery):
#   docker run -d --name openvideomenu --network host \
#     -v /path/to/media:/media:ro -e PLEX_URL=http://127.0.0.1:32400 openvideomenu-asset-server

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
RUN npx tsc -b packages/core packages/asset-server

FROM node:24-alpine
WORKDIR /app
ENV NODE_ENV=production
COPY package.json package-lock.json ./
COPY packages/core/package.json packages/core/
COPY packages/asset-server/package.json packages/asset-server/
COPY packages/renderer-web/package.json packages/renderer-web/
COPY packages/cli/package.json packages/cli/
RUN npm ci --omit=dev --ignore-scripts -w @openvideomenu/asset-server && npm cache clean --force
COPY --from=build /src/packages/core/dist packages/core/dist
COPY --from=build /src/packages/asset-server/dist packages/asset-server/dist
ENV MEDIA_ROOTS=/media PORT=8790
EXPOSE 8790
# Runs as the unprivileged "node" user; the media mount should be read-only (:ro).
USER node
CMD ["node", "packages/asset-server/dist/main.js"]
