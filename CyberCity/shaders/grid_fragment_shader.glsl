#version 330 core
out vec4 FragColor; // Output color of the fragment

// Uniforms (data passed from Python to GLSL)
uniform float iTime; // Current time in seconds
uniform vec2 iResolution; // Viewport resolution (width, height)

// New camera uniforms
uniform vec3 u_cameraPosition;
uniform vec3 u_cameraTarget;
uniform vec3 u_cameraUp;

// --- Constants (copied from your original GLSL) ---
#define DISTANCE_EPSILON 0.01
#define NORMAL_DELTA 0.0001
#define OCCLUSION_DELTA 0.1
#define SCENE_RADIUS 500.0 // This constant is not used in the provided code
#define STEP_SCALE 1.0

#define MAX_HIT_COUNT 5
#define ITERATION_COUNT 100

#define TYPE_NONE        0
#define TYPE_OPAQUE      1
#define TYPE_TRANSPARENT 2

#define MODE_OUTSIDE 0
#define MODE_INSIDE  1

#define N1 1.0 // Refractive index of low 1 (e.g., air)
uniform float N2; // Refractive index of medium 2 (e.g., acrylic), now a uniform

#define R0(x, y) (((x - y) / (x + y)) * ((x - y) / (x + y)))
#define PI 3.14159265359

// --- Structures (translated to GLSL structs) ---
struct Material {
    vec3 color;
    float shininess;
    float opacity;
};

struct HitInfo {
    Material material;
    int type;
};

HitInfo hitInfo; // Global HitInfo for the current ray trace

// --- Helper Functions (copied directly from your GLSL) ---
vec3 mirrorRay(vec3 ray, vec3 normal) {
    float dot_val = dot(ray, normal);
    return 2.0 * dot_val * normal - ray;
}

float getPatternWeight(vec3 position) {
    float grid_line_width = 0.0125;
    // Grid lines every 2 units (at 0, 2, 4, 6, ...)
    vec2 frac_xz = fract(position.xz / 2.0);
    float is_grid_line = 0.0;
    if (frac_xz.x < grid_line_width || frac_xz.x > (1.0 - grid_line_width)) {
        is_grid_line = 1.0;
    }
    if (frac_xz.y < grid_line_width || frac_xz.y > (1.0 - grid_line_width)) {
        is_grid_line = 1.0;
    }
    return is_grid_line;
}

float getDistanceBox(vec3 position, vec3 boxRadius, float r) {
    vec3 delta = abs(position) - boxRadius;
    return min(max(delta.x, max(delta.y, delta.z)), 0.0) + length(max(delta, 0.0)) - r;
}

float getDistancePlaneXZ(vec3 position, float height) {
    return position.y - height;
}

mat2 getRotation(float r) {
   return mat2(cos(r), sin(r), -sin(r), cos(r));
}

// --- Scene SDFs ---
float getDistanceSceneOpaque(vec3 position, bool saveHit, inout HitInfo hInfo) {
    float field = getDistancePlaneXZ(position, 0.0);

    if (field < DISTANCE_EPSILON && saveHit) {
        hInfo.type = TYPE_OPAQUE;
        hInfo.material.color = mix(
            vec3(0.0, 0.0, 0.0),
            vec3(0.20, 0.0, 1.0),
            getPatternWeight(position)
        );
        hInfo.material.shininess = 1.0;
        hInfo.material.opacity = 1.0;
    }
    return field;
}

// MODIFIED: To produce a 10x10 grid of boxes, with spacing equal to building width,
// and buildings centered in the "black squares" (between grid lines).
float getDistanceSceneTransparent(vec3 position, bool saveHit, inout HitInfo hInfo, float clock_val, vec3 target_val) {
    // Building dimensions: 2 units wide in XZ (radius 1.0), 6 units tall (radius 3.0).
    vec3 single_box_radius = vec3(1.0, 3.0, 1.0);

    // Effective block size: Building width + spacing width = 2.0 + 2.0 = 4.0 units.
    // This means each building occupies a 4x4 conceptual block.
    float effective_block_size = 4.0;

    // Offset to center the building within a "black square" (e.g., 0-2, 4-6, etc.)
    // If grid lines are at 0, 2, 4, ..., a black square is 0-2. Its center is 1.
    // So, we need to shift the coordinates by 1.0 to align the building's center.
    float offset_to_center_in_black_square = 1.0;

    // Calculate the grid cell coordinates for the current position
    // These coordinates define the 4x4 blocks.
    vec2 grid_coord_xz = floor(position.xz / effective_block_size);

    // Calculate the grid cell coordinates for the camera position
    vec2 camera_grid_center = floor(u_cameraPosition.xz / effective_block_size);

    // Get the local position within the current effective block.
    // We shift the 'position.xz' by 'offset_to_center_in_black_square' before
    // applying the 'mod' operation to align the repeating pattern correctly.
    // Then, we center the result by subtracting half of the effective_block_size.
    vec3 local_position = vec3(
        mod(position.x - offset_to_center_in_black_square + 0.5 * effective_block_size, effective_block_size) - 0.5 * effective_block_size,
        position.y - single_box_radius.y, // Adjust Y to place building base at Y=0
        mod(position.z - offset_to_center_in_black_square + 0.5 * effective_block_size, effective_block_size) - 0.5 * effective_block_size
    );

    float field = getDistanceBox(local_position, single_box_radius, 0.01);

    // Check if we're within a 10x10 grid of these 4x4 blocks centered on the camera.
    // This creates a 10x10 array of buildings.
    vec2 relative_grid = abs(grid_coord_xz - camera_grid_center);

    if (relative_grid.x < 50.0 && relative_grid.y < 50.0 && // 50 blocks in each direction from center = 100x100 total
        field < DISTANCE_EPSILON && saveHit) {
        hInfo.type = TYPE_TRANSPARENT;
        hInfo.material.color = vec3(1.0, 1.0, 1.0);
        hInfo.material.shininess = 32.0;
        hInfo.material.opacity = 0.02;
    }
    return field;
}

float getDistanceScene(vec3 position, bool saveHit, inout HitInfo hInfo, float clock_val, vec3 target_val) {
    if (saveHit) {
        hInfo.type = TYPE_NONE;
    }

    float dist_transparent = getDistanceSceneTransparent(position, saveHit, hInfo, clock_val, target_val);
    float dist_opaque = getDistanceSceneOpaque(position, saveHit, hInfo);

    return min(dist_opaque, dist_transparent);
}

// --- Normal Calculation ---
vec3 getNormalTransparent(vec3 position, float clock_val, vec3 target_val) {
    HitInfo temp_hit_info;
    vec3 nDelta = vec3(
        getDistanceSceneTransparent(position - vec3(NORMAL_DELTA, 0.0, 0.0), false, temp_hit_info, clock_val, target_val),
        getDistanceSceneTransparent(position - vec3(0.0, NORMAL_DELTA, 0.0), false, temp_hit_info, clock_val, target_val),
        getDistanceSceneTransparent(position - vec3(0.0, 0.0, NORMAL_DELTA), false, temp_hit_info, clock_val, target_val)
    );

    vec3 pDelta = vec3(
        getDistanceSceneTransparent(position + vec3(NORMAL_DELTA, 0.0, 0.0), false, temp_hit_info, clock_val, target_val),
        getDistanceSceneTransparent(position + vec3(0.0, NORMAL_DELTA, 0.0), false, temp_hit_info, clock_val, target_val),
        getDistanceSceneTransparent(position + vec3(0.0, 0.0, NORMAL_DELTA), false, temp_hit_info, clock_val, target_val)
    );
    return normalize(pDelta - nDelta);
}

vec3 getNormalOpaque(vec3 position) {
    HitInfo temp_hit_info;
    vec3 nDelta = vec3(
        getDistanceSceneOpaque(position - vec3(NORMAL_DELTA, 0.0, 0.0), false, temp_hit_info),
        getDistanceSceneOpaque(position - vec3(0.0, NORMAL_DELTA, 0.0), false, temp_hit_info),
        getDistanceSceneOpaque(position - vec3(0.0, 0.0, NORMAL_DELTA), false, temp_hit_info)
    );

    vec3 pDelta = vec3(
        getDistanceSceneOpaque(position + vec3(NORMAL_DELTA, 0.0, 0.0), false, temp_hit_info),
        getDistanceSceneOpaque(position + vec3(0.0, NORMAL_DELTA, 0.0), false, temp_hit_info),
        getDistanceSceneOpaque(position + vec3(0.0, 0.0, NORMAL_DELTA), false, temp_hit_info)
    );
    return normalize(pDelta - nDelta);
}

// --- Lighting Components ---
float getSoftShadow(vec3 position, vec3 normal, vec3 target_val, float clock_val) {
    position += DISTANCE_EPSILON * normal;
    float delta = 1.0;
    float minimum = 1.0;
    vec3 lightPosition = vec3(0.0, 20.0, 0.0);
    vec3 lightNormal = normalize(lightPosition - position);

    for (int i = 0; i < ITERATION_COUNT; i++) {
        HitInfo temp_hit_info;
        float field = max(0.0, getDistanceSceneTransparent(position, false, temp_hit_info, clock_val, target_val));
        if (field < DISTANCE_EPSILON) return 0.3;
        minimum = min(minimum, 8.0 * field / delta);
        delta += 0.1 * field;
        position += field * 0.25 * lightNormal;
    }
    return clamp(minimum, 0.3, 1.0);
}

float getAmbientOcclusion(vec3 position, vec3 normal, float clock_val, vec3 target_val) {
    float brightness = 0.0;
    HitInfo temp_hit_info;
    brightness += getDistanceScene(position + 0.5 * OCCLUSION_DELTA * normal, false, temp_hit_info, clock_val, target_val);
    brightness += getDistanceScene(position + 2.0 * OCCLUSION_DELTA * normal, false, temp_hit_info, clock_val, target_val);
    brightness += getDistanceScene(position + 4.0 * OCCLUSION_DELTA * normal, false, temp_hit_info, clock_val, target_val);

    brightness = pow(max(0.0, brightness + 0.01), 0.5);
    return clamp(1.0 * brightness, 0.5, 1.0);
}

vec3 getLightDiffuse(vec3 surfaceNormal, vec3 lightNormal, vec3 lightColor) {
    float power = max(0.0, dot(surfaceNormal, lightNormal));
    return power * lightColor;
}

vec3 getLightSpecular(vec3 reflectionNormal, vec3 viewNormal, vec3 lightColor, float power) {
    return pow(max(0.0, dot(reflectionNormal, viewNormal)), power) * lightColor;
}

float getFresnelSchlick(vec3 surfaceNormal, vec3 halfWayNormal, float r0) {
    return r0 + (1.0 - r0) * pow(1.0 - dot(surfaceNormal, halfWayNormal), 5.0);
}

vec3 computeLight(vec3 position, vec3 surfaceNormal, Material material, vec3 viewer_val, vec3 target_val, float clock_val) {
    vec3 lightPosition = vec3(u_cameraPosition);  // Light follows the camera
    vec3 lightAmbient  = 1.0 * vec3(0.2, 0.2, 0.2);
    vec3 lightColor    = 1000.0 * vec3(1.0, 1.0, 1.0);

    vec3 lightVector = lightPosition - position;
    float attenuation = 1.0 / dot(lightVector, lightVector);
    if (dot(surfaceNormal, lightVector) <= 0.0) return lightAmbient * material.color;

    vec3 lightNormal = normalize(lightVector);

    if (material.opacity == 1.0) {
        lightColor *= getSoftShadow(position, lightNormal, target_val, clock_val);
    }

    vec3 viewNormal       = normalize(viewer_val - position);
    vec3 halfWayNormal    = normalize(viewNormal + lightNormal);
    vec3 reflectionNormal = mirrorRay(lightNormal, surfaceNormal);

    float fresnelTerm  = getFresnelSchlick(surfaceNormal, halfWayNormal, material.shininess);
    vec3 lightDiffuse  = getLightDiffuse(surfaceNormal, lightNormal, lightColor);
    vec3 lightSpecular = getLightSpecular(reflectionNormal, viewNormal, lightColor, 512.0);

    float brightness = getAmbientOcclusion(position, surfaceNormal, clock_val, target_val);

    return brightness * (
        lightAmbient + attenuation * (
            material.opacity * lightDiffuse + fresnelTerm * lightSpecular
        )
    ) * material.color;
}

vec3 getSkyColor(vec3 ray_dir) {
    vec3 sky_top = vec3(0.0, 0.0, 0.0);
    vec3 sky_bottom = vec3(0.05, 0.0, 0.1);

    float t = (ray_dir.y + 1.0) * 0.99;
    return mix(sky_bottom, sky_top, t);
}

// --- Main Ray Tracing (Ray Marching) Function ---
vec3 traceRay(vec3 position, vec3 normal, vec3 viewer_val, vec3 target_val, float clock_val) {
    vec3 colorOutput = vec3(0.0);
    vec3 rayColor = vec3(1.0);

    int mode = MODE_OUTSIDE;

    for(int hitCount = 0; hitCount < MAX_HIT_COUNT; hitCount++) {
        hitInfo.type = TYPE_NONE;
        hitInfo.material.color = vec3(0.0);

        for (int it = 0; it < ITERATION_COUNT; it++) {
            float field;

            if (mode == MODE_OUTSIDE) {
                field = getDistanceScene(position, true, hitInfo, clock_val, target_val);
                if (field < DISTANCE_EPSILON) break;
            }
            else {
                field = getDistanceSceneTransparent(position, true, hitInfo, clock_val, target_val);
                if (field > DISTANCE_EPSILON) break;
            }

            float march = max(DISTANCE_EPSILON, abs(field));
            position = position + STEP_SCALE * march * normal;
        }

        if (hitInfo.type == TYPE_OPAQUE) {
            colorOutput += rayColor * computeLight(
                position,
                getNormalOpaque(position),
                hitInfo.material,
                viewer_val, target_val, clock_val
            );
            break;
        }
        else if (hitInfo.type == TYPE_TRANSPARENT) {
            vec3 surfaceNormal = getNormalTransparent(position, clock_val, target_val);
            if (mode == MODE_INSIDE) surfaceNormal = -surfaceNormal;

            colorOutput += 0.0 * rayColor * computeLight(
                position,
                surfaceNormal,
                hitInfo.material,
                viewer_val, target_val, clock_val
            );

            rayColor *= hitInfo.material.color;
            normal = refract(normal, surfaceNormal, N1 / N2);

            if (mode == MODE_INSIDE) {
                if (dot(normal, surfaceNormal) < 0.0) mode = MODE_OUTSIDE;
                else                                   mode = MODE_INSIDE;
            }
            else mode = MODE_INSIDE;
        }
        else {
            colorOutput += rayColor * getSkyColor(normal);
            break;
        }
    }
    return colorOutput;
}

vec3 createRayNormal(vec3 origo, vec3 target, vec3 up, vec2 plane, float fov, vec3 displacement) {
    vec3 axisZ = normalize(target - origo);
    vec3 axisX = normalize(cross(axisZ, up));
    vec3 axisY = cross(axisX, axisZ);

    origo += mat3(axisX, axisY, axisZ) * -displacement;

    vec3 point = target + fov * length(target - origo) * (plane.x * axisX + plane.y * axisY);
    return normalize(point - origo);
}

void main() {
    vec3 viewer_val = u_cameraPosition;
    vec3 target_val = u_cameraTarget;
    vec3 up_val = u_cameraUp;

    vec3 displacement = vec3(0.0);

    vec3 normal = createRayNormal(
        viewer_val,
        target_val,
        up_val,
        (gl_FragCoord.xy - 0.5 * iResolution.xy) / iResolution.y,
        1.6, // FOV
        displacement
    );

    vec3 fixed_cube_target = vec3(0.0, 0.5, 0.0); // This target is not used for grid positioning, but for the fixed cube reference
    vec3 color = traceRay(viewer_val, normal, viewer_val, fixed_cube_target, 0.0);

    FragColor = vec4(pow(color, vec3(1.0 / 2.2)), 1.0);
}
