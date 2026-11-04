# Simulated cameras: 5 looping H.264 clips built at image-build time from assets/,
# then 20 RTSP publishers (-c copy, no re-encode) supervised at runtime.
FROM jrottenberg/ffmpeg:7.0-alpine

ENTRYPOINT []
WORKDIR /sim
COPY assets/ /sim/assets/
COPY build-videos.sh supervisor.sh /sim/
RUN sed -i 's/\r$//' /sim/*.sh && chmod +x /sim/*.sh \
 && /sim/build-videos.sh /sim/assets /sim/videos \
 && rm -rf /sim/assets \
 && adduser -D -u 10001 sim && chown -R sim /sim
USER sim
CMD ["/sim/supervisor.sh"]
