# Admin console (apps/admin) as a static site: Vite build -> Caddy.
#
# The build context is the REPOSITORY ROOT (pnpm workspace, packages/contracts-ts):
#   docker build -f apps/admin/Dockerfile --build-arg VITE_ADMIN_API=https://<server>/api/v1 -t clubshell-admin .
# Railway: a service variable VITE_ADMIN_API reaches the build through the ARG below; RAILWAY_DOCKERFILE_PATH=apps/admin/Dockerfile.
# The server must list this site's origin in Cors__AllowedOrigins__0 (server/README.md, "Reverse proxy").

FROM node:20-alpine AS build
WORKDIR /src
RUN corepack enable && corepack prepare pnpm@9.9.0 --activate

# Every workspace manifest: a frozen install checks the whole lockfile, even when it installs one project.
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml tsconfig.base.json ./
COPY apps/admin/package.json apps/admin/
COPY apps/shell/package.json apps/shell/
COPY packages/contracts-ts/package.json packages/contracts-ts/
COPY tools/MockServer/package.json tools/MockServer/
COPY tests/shell-e2e/package.json tests/shell-e2e/
RUN pnpm install --frozen-lockfile --filter "@clubshell/admin..."

COPY packages/contracts-ts/ packages/contracts-ts/
COPY apps/admin/ apps/admin/

# Baked into the bundle (import.meta.env): the API base of the central server, e.g. https://club.example.uz/api/v1.
ARG VITE_ADMIN_API
RUN test -n "$VITE_ADMIN_API" || { echo "VITE_ADMIN_API build argument is required" >&2; exit 1; }
ENV VITE_ADMIN_API=$VITE_ADMIN_API
RUN pnpm --filter @clubshell/admin build

FROM caddy:2-alpine AS final
COPY apps/admin/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /src/apps/admin/dist /srv
# Caddy listens on $PORT (Railway) or 8080.
ENV PORT=8080
EXPOSE 8080
