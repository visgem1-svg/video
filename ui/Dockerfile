FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
# The API base is baked in at build time (browser talks to the gateway directly).
ARG VITE_API_BASE=http://localhost:8000
ARG VITE_MOCK=0
ENV VITE_API_BASE=$VITE_API_BASE VITE_MOCK=$VITE_MOCK
RUN npm run build

FROM nginx:alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
HEALTHCHECK CMD wget -qO- http://127.0.0.1/healthz || exit 1
