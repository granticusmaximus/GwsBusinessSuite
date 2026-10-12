// Overwatch's own street view: a 360° panorama viewer (no third-party viewer library).
//
// Each panorama is an equirectangular photo (Mapillary, served same-origin by
// /admin/api/overwatch/streetview/{id}). A full-screen WebGL fragment shader turns every screen
// pixel into a viewing direction and samples the photo at that direction's longitude/latitude -
// the photo is effectively wrapped around the inside of a sphere with you at the centre. Drag to
// look around, scroll/pinch to zoom, and click an arrow on the ground to walk to the next
// panorama (the page answers MoveStreetViewAsync with the new scene).
//
// Angles: yaw is the compass heading you're looking at (0 = north, 90 = east); pitch is up/down;
// a panorama's compassAngle is the heading the middle of its photo faces.
window.gwsStreetView = (function () {
    const MIN_FOV = 30, MAX_FOV = 100, MAX_PITCH = 85;
    let view = null;

    const vertexSource = `
        attribute vec2 a_position;
        varying vec2 v_position;
        void main() { v_position = a_position; gl_Position = vec4(a_position, 0.0, 1.0); }`;

    const fragmentSource = `
        precision highp float;
        uniform sampler2D u_texture;
        uniform float u_yaw, u_pitch, u_fov, u_aspect, u_compass;
        varying vec2 v_position;
        void main() {
            float t = tan(u_fov * 0.5);
            vec3 dir = normalize(vec3(v_position.x * t * u_aspect, v_position.y * t, 1.0));
            float cp = cos(u_pitch), sp = sin(u_pitch);
            dir = vec3(dir.x, dir.y * cp + dir.z * sp, -dir.y * sp + dir.z * cp);
            float cy = cos(u_yaw), sy = sin(u_yaw);
            dir = vec3(dir.x * cy + dir.z * sy, dir.y, -dir.x * sy + dir.z * cy);
            float lon = atan(dir.x, dir.z);
            float lat = asin(clamp(dir.y, -1.0, 1.0));
            float u = fract(0.5 + (lon - u_compass) / 6.28318530718);
            float v = 0.5 - lat / 3.14159265359;
            gl_FragColor = texture2D(u_texture, vec2(u, v));
        }`;

    function compile(gl, type, source) {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader) || 'shader error');
        return shader;
    }

    function createRenderer(canvas) {
        const gl = canvas.getContext('webgl', { antialias: false, preserveDrawingBuffer: true });
        if (!gl) throw new Error('This browser has no WebGL, which street view needs.');
        const program = gl.createProgram();
        gl.attachShader(program, compile(gl, gl.VERTEX_SHADER, vertexSource));
        gl.attachShader(program, compile(gl, gl.FRAGMENT_SHADER, fragmentSource));
        gl.linkProgram(program);
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) || 'link error');
        gl.useProgram(program);
        const buffer = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
        const position = gl.getAttribLocation(program, 'a_position');
        gl.enableVertexAttribArray(position);
        gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);
        const texture = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, texture);
        // Panoramas aren't power-of-two sized: clamp + no mipmaps (the shader wraps longitude itself).
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        const uniforms = {};
        ['u_texture', 'u_yaw', 'u_pitch', 'u_fov', 'u_aspect', 'u_compass'].forEach(name => { uniforms[name] = gl.getUniformLocation(program, name); });
        return { gl, uniforms, texture, hasImage: false };
    }

    const radians = degrees => degrees * Math.PI / 180;
    const normalize = degrees => ((degrees % 360) + 360) % 360;
    // -180..180: how far right (+) or left (-) of where you're looking.
    const relative = degrees => { const d = normalize(degrees); return d > 180 ? d - 360 : d; };

    function render() {
        if (!view || view.frame) return;
        view.frame = requestAnimationFrame(() => {
            view.frame = 0;
            const { canvas, renderer, state } = view;
            const dpr = Math.min(window.devicePixelRatio || 1, 2);
            const width = Math.max(1, Math.round(canvas.clientWidth * dpr));
            const height = Math.max(1, Math.round(canvas.clientHeight * dpr));
            if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
            const { gl, uniforms } = renderer;
            gl.viewport(0, 0, width, height);
            if (renderer.hasImage) {
                gl.uniform1i(uniforms.u_texture, 0);
                gl.uniform1f(uniforms.u_yaw, radians(state.yaw));
                gl.uniform1f(uniforms.u_pitch, radians(state.pitch));
                gl.uniform1f(uniforms.u_fov, radians(state.fov));
                gl.uniform1f(uniforms.u_aspect, width / height);
                gl.uniform1f(uniforms.u_compass, radians(state.compass));
                gl.drawArrays(gl.TRIANGLES, 0, 3);
            } else {
                gl.clearColor(0, 0, 0, 1);
                gl.clear(gl.COLOR_BUFFER_BIT);
            }
            placeArrows();
            if (view.compass) view.compass.style.transform = `rotate(${-state.yaw}deg)`;
        });
    }

    // Arrows sit on an ellipse on the "ground" below the middle of the view: straight ahead is the
    // far (upper) side of the ellipse, behind you the near side, each rotated to point its way.
    function placeArrows() {
        const { arrows, state, links } = view;
        if (!arrows) return;
        links.forEach(link => {
            const rel = relative(link.bearing - state.yaw);
            const button = link.element;
            const x = 50 + Math.sin(radians(rel)) * 16;
            const y = 80 - Math.cos(radians(rel)) * 7;
            button.style.left = `${x}%`;
            button.style.top = `${y}%`;
            button.style.transform = `translate(-50%, -50%) rotate(${rel}deg)`;
            button.dataset.relative = String(Math.round(rel));
        });
    }

    function setStatus(text) {
        if (view && view.status) {
            view.status.textContent = text || '';
            view.status.hidden = !text;
        }
    }

    function buildArrows(scene) {
        view.arrows.replaceChildren();
        view.links = (scene.links || []).map(link => {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'tg-sv-arrow';
            const heading = ['north', 'northeast', 'east', 'southeast', 'south', 'southwest', 'west', 'northwest'][Math.round(normalize(link.bearing) / 45) % 8];
            button.setAttribute('aria-label', `Walk ${heading}, ${Math.round(link.distanceMeters)} m`);
            button.title = button.getAttribute('aria-label');
            button.dataset.panoramaId = link.id;
            button.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3l8 14h-5v4H9v-4H4z"/></svg>';
            button.addEventListener('click', () => move(link.id));
            view.arrows.appendChild(button);
            return { id: link.id, bearing: link.bearing, distanceMeters: link.distanceMeters, element: button };
        });
        // Place them now, not on the next frame, so they never sit unplaced in the corner.
        placeArrows();
    }

    function loadScene(scene, keepHeading) {
        const current = scene.current;
        view.scene = scene;
        view.state.compass = Number(current.compassAngle) || 0;
        if (!keepHeading) { view.state.yaw = normalize(Number(scene.initialHeading ?? current.compassAngle) || 0); view.state.pitch = 0; }
        buildArrows(scene);
        setStatus('Loading the panorama...');
        const image = new Image();
        image.decoding = 'async';
        const requested = current.id;
        image.onload = () => {
            if (!view || view.scene.current.id !== requested) return;
            const { gl } = view.renderer;
            gl.bindTexture(gl.TEXTURE_2D, view.renderer.texture);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGB, gl.RGB, gl.UNSIGNED_BYTE, image);
            view.renderer.hasImage = true;
            view.loadedId = requested;
            setStatus('');
            render();
        };
        image.onerror = () => { if (view && view.scene.current.id === requested) setStatus("This panorama couldn't be loaded."); };
        image.src = scene.imageUrl;
        render();
    }

    async function move(id) {
        if (!view || view.moving) return;
        view.moving = true;
        setStatus('Walking...');
        try {
            const scene = await view.dotNetRef.invokeMethodAsync('MoveStreetViewAsync', id);
            if (view && scene) loadScene(scene, true);
            else setStatus("Couldn't walk there.");
        } catch {
            setStatus("Couldn't walk there.");
        } finally {
            if (view) view.moving = false;
        }
    }

    function attachInput() {
        const { canvas, state } = view;
        const pointers = new Map();
        let pinchDistance = 0;
        const degreesPerPixel = () => state.fov / Math.max(1, canvas.clientHeight);
        view.onPointerDown = event => {
            canvas.setPointerCapture(event.pointerId);
            pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
            canvas.classList.add('is-dragging');
        };
        view.onPointerMove = event => {
            const previous = pointers.get(event.pointerId);
            if (!previous) return;
            const next = { x: event.clientX, y: event.clientY };
            pointers.set(event.pointerId, next);
            if (pointers.size === 2) {
                const [a, b] = [...pointers.values()];
                const distance = Math.hypot(a.x - b.x, a.y - b.y);
                if (pinchDistance) zoomBy(pinchDistance / distance);
                pinchDistance = distance;
                return;
            }
            state.yaw = normalize(state.yaw - (next.x - previous.x) * degreesPerPixel());
            state.pitch = Math.max(-MAX_PITCH, Math.min(MAX_PITCH, state.pitch + (next.y - previous.y) * degreesPerPixel()));
            render();
        };
        view.onPointerUp = event => {
            pointers.delete(event.pointerId);
            if (pointers.size < 2) pinchDistance = 0;
            if (pointers.size === 0) canvas.classList.remove('is-dragging');
        };
        view.onWheel = event => { event.preventDefault(); zoomBy(1 + event.deltaY * 0.001); };
        view.onKey = event => {
            const step = 5;
            if (event.key === 'ArrowLeft') state.yaw = normalize(state.yaw - step);
            else if (event.key === 'ArrowRight') state.yaw = normalize(state.yaw + step);
            else if (event.key === 'ArrowUp') state.pitch = Math.min(MAX_PITCH, state.pitch + step);
            else if (event.key === 'ArrowDown') state.pitch = Math.max(-MAX_PITCH, state.pitch - step);
            else if (event.key === '+' || event.key === '=') zoomBy(0.9);
            else if (event.key === '-') zoomBy(1.1);
            else if (event.key === 'Escape') { view.dotNetRef.invokeMethodAsync('CloseStreetViewAsync').catch(() => {}); return; }
            else return;
            event.preventDefault();
            render();
        };
        canvas.addEventListener('pointerdown', view.onPointerDown);
        canvas.addEventListener('pointermove', view.onPointerMove);
        canvas.addEventListener('pointerup', view.onPointerUp);
        canvas.addEventListener('pointercancel', view.onPointerUp);
        canvas.addEventListener('wheel', view.onWheel, { passive: false });
        canvas.addEventListener('keydown', view.onKey);
        view.resizeObserver = new ResizeObserver(render);
        view.resizeObserver.observe(canvas);
    }

    function zoomBy(factor) {
        view.state.fov = Math.max(MIN_FOV, Math.min(MAX_FOV, view.state.fov * factor));
        render();
    }

    // host: the .tg-sv element (contains .tg-sv-canvas, .tg-sv-arrows, .tg-sv-compass, .tg-sv-status).
    // scene: { current: {id, latitude, longitude, compassAngle, capturedAt}, links: [{id, bearing, distanceMeters}],
    //          imageUrl, initialHeading? }
    function open(hostId, dotNetRef, scene) {
        close();
        const host = document.getElementById(hostId);
        if (!host) return false;
        const canvas = host.querySelector('.tg-sv-canvas');
        view = {
            host, canvas, dotNetRef,
            arrows: host.querySelector('.tg-sv-arrows'),
            compass: host.querySelector('.tg-sv-compass-needle'),
            status: host.querySelector('.tg-sv-status'),
            state: { yaw: 0, pitch: 0, fov: 80, compass: 0 },
            links: [], frame: 0, moving: false
        };
        try {
            view.renderer = createRenderer(canvas);
        } catch (error) {
            setStatus(error.message);
            return false;
        }
        attachInput();
        loadScene(scene, false);
        canvas.focus({ preventScroll: true });
        return true;
    }

    function close() {
        if (!view) return;
        const { canvas } = view;
        if (view.frame) cancelAnimationFrame(view.frame);
        canvas.removeEventListener('pointerdown', view.onPointerDown);
        canvas.removeEventListener('pointermove', view.onPointerMove);
        canvas.removeEventListener('pointerup', view.onPointerUp);
        canvas.removeEventListener('pointercancel', view.onPointerUp);
        canvas.removeEventListener('wheel', view.onWheel);
        canvas.removeEventListener('keydown', view.onKey);
        view.resizeObserver?.disconnect();
        view = null;
    }

    // For tests and the page: where you're looking and which panorama is showing.
    function getState() {
        if (!view) return null;
        return {
            yaw: view.state.yaw, pitch: view.state.pitch, fov: view.state.fov,
            panoramaId: view.scene && view.scene.current.id, loadedId: view.loadedId || null,
            links: view.links.map(link => ({ id: link.id, relative: Math.round(relative(link.bearing - view.state.yaw)) }))
        };
    }

    function look(yaw, pitch) {
        if (!view) return;
        view.state.yaw = normalize(yaw);
        view.state.pitch = Math.max(-MAX_PITCH, Math.min(MAX_PITCH, pitch || 0));
        render();
    }

    return { open, close, getState, look };
})();
