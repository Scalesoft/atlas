# syntax=docker/dockerfile:1
# Atlas PostgreSQL: the official base postgres:18.6-alpine plus pgvector, nothing else. Same Alpine/musl and the same
# PostgreSQL major as the former postgres:18-alpine, so an existing data volume is reused as-is (no dump, no REINDEX).
# Image tag: <POSTGRES_VERSION>-pgvector<PGVECTOR_VERSION>[-<IMAGE_REVISION>], e.g. 18.6-pgvector0.8.7-r2; .github/workflows/release.yml
# derives it from the ARG defaults below. Bump IMAGE_REVISION (r2, r3, ...) for any other change to this file.
ARG POSTGRES_VERSION=18.6
ARG POSTGRES_BASE=docker.io/library/postgres:18.6-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873
ARG PGVECTOR_VERSION=0.8.7
ARG PGVECTOR_COMMIT=f37c13f68b57d2c3472b2214fbcff699d6d34876
ARG IMAGE_REVISION=r2

# A short C build for the target architecture (on an ARM64 host: emulated for linux/amd64). It is not the .NET SDK,
# which AGENTS.md keeps on $BUILDPLATFORM; cross-compiling against the target's PGXS on Alpine would need a musl cross toolchain.
FROM ${POSTGRES_BASE} AS build
ARG PGVECTOR_VERSION
ARG PGVECTOR_COMMIT
RUN apk add --no-cache --virtual .build build-base git
WORKDIR /src
RUN git -c advice.detachedHead=false clone --quiet --branch "v${PGVECTOR_VERSION}" --depth 1 https://github.com/pgvector/pgvector.git . \
    && test "$(git rev-parse HEAD)" = "${PGVECTOR_COMMIT}" \
    || { echo "pgvector v${PGVECTOR_VERSION} is not commit ${PGVECTOR_COMMIT}" >&2; exit 1; }
# OPTFLAGS="" drops -march=native, so the binary runs on any CPU of the architecture (pgvector dispatches SIMD at run
# time); with_llvm=no skips JIT bitcode, which would need clang and LLVM in this stage.
RUN make OPTFLAGS="" with_llvm=no \
    && make OPTFLAGS="" with_llvm=no install \
    && mkdir -p /out/lib /out/extension \
    && cp "$(pg_config --pkglibdir)/vector.so" /out/lib/ \
    && cp "$(pg_config --sharedir)/extension/vector.control" "$(pg_config --sharedir)"/extension/vector--*.sql /out/extension/

FROM ${POSTGRES_BASE}
ARG POSTGRES_VERSION
ARG POSTGRES_BASE
ARG PGVECTOR_VERSION
ARG PGVECTOR_COMMIT
ARG IMAGE_REVISION
COPY --from=build /out/lib/ /usr/local/lib/postgresql/
COPY --from=build /out/extension/ /usr/local/share/postgresql/extension/
LABEL org.opencontainers.image.title="atlas-postgres" \
      org.opencontainers.image.description="PostgreSQL ${POSTGRES_VERSION} (Alpine) with pgvector ${PGVECTOR_VERSION} for Atlas" \
      org.opencontainers.image.version="${POSTGRES_VERSION}-pgvector${PGVECTOR_VERSION}${IMAGE_REVISION:+-}${IMAGE_REVISION}" \
      org.opencontainers.image.source="https://github.com/Scalesoft/atlas" \
      org.opencontainers.image.base.name="${POSTGRES_BASE}" \
      org.atlas.pgvector.version="${PGVECTOR_VERSION}" \
      org.atlas.pgvector.source="https://github.com/pgvector/pgvector/tree/${PGVECTOR_COMMIT}"
# Set by the build: the commit (release.yml) and the git blob of this Dockerfile (build.ps1 sets `working-tree`) the image was built from.
ARG SOURCE_REVISION=
ARG DOCKERFILE_BLOB=
LABEL org.opencontainers.image.revision="${SOURCE_REVISION}" \
      org.atlas.postgres.dockerfile="${DOCKERFILE_BLOB}"
