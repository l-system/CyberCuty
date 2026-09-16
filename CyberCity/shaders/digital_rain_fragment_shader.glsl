#version 330 core
out vec4 FragColor;

uniform float iTime;
uniform vec2 iResolution;
uniform vec3 u_cameraPosition;
uniform vec3 u_cameraTarget;
uniform vec3 u_cameraUp;
uniform sampler2D u_fontAtlas;
uniform float u_atlasWidth;
uniform float u_atlasHeight;
uniform float u_distanceRange;
uniform vec4 u_charPlaneBounds[16];
uniform vec4 u_charAtlasBounds[16];
uniform float N2;

#define DISTANCE_EPSILON 0.01
#define NORMAL_DELTA 0.0001
#define OCCLUSION_DELTA 0.1
#define SCENE_RADIUS 500.0
#define STEP_SCALE 1.0
#define MAX_HIT_COUNT 5
#define ITERATION_COUNT 100
#define TYPE_NONE 0
#define TYPE_OPAQUE 1
#define TYPE_TRANSPARENT 2
#define MODE_OUTSIDE 0
#define MODE_INSIDE 1
#define N1 1.0
#define R0(x, y) (((x - y) / (x + y)) * ((x - y) / (x + y)))
#define PI 3.14159265359
#define CAMERA_FAR_PLANE_DISTANCE 1000.0

struct Material {
    vec3 color;
    float shininess;
    float opacity;
    vec3 emissive;
    float textAlpha;
};

struct HitInfo {
    Material material;
    int type;
};

HitInfo hitInfo;

vec3 mirrorRay(vec3 ray, vec3 normal) {
    float dot_val = dot(ray, normal);
    return 2.0 * dot_val * normal - ray;
}

float getPatternWeight(vec3 position) {
    float grid_line_width = 0.0125;
    vec2 frac_xz = fract(position.xz / 2.0);
    float is_grid_line = 0.0;
    if (frac_xz.x < grid_line_width || frac_xz.x > (1.0 - grid_line_width)) is_grid_line = 1.0;
    if (frac_xz.y < grid_line_width || frac_xz.y > (1.0 - grid_line_width)) is_grid_line = 1.0;
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

float median(float r, float g, float b) {
    return max(min(r, g), min(max(r, g), b));
}

float drawCharacter(int char_index, vec2 uv) {
    vec4 planeBounds = u_charPlaneBounds[char_index];
    vec4 atlasBounds = u_charAtlasBounds[char_index];

    vec2 glyph_pos_in_plane = mix(planeBounds.xy, planeBounds.zw, uv);
    vec2 atlas_pixel_coords = atlasBounds.xy + (glyph_pos_in_plane - planeBounds.xy) *
                              (atlasBounds.zw - atlasBounds.xy) / (planeBounds.zw - planeBounds.xy);
    vec2 atlas_uv = atlas_pixel_coords / vec2(u_atlasWidth, u_atlasHeight);

    vec3 msdf_sample = texture(u_fontAtlas, atlas_uv).rgb;
    float signed_distance = median(msdf_sample.r, msdf_sample.g, msdf_sample.b) - 0.5;
    return clamp(signed_distance / fwidth(signed_distance) + 0.5, 0.0, 1.0);
}

float getDistanceSceneOpaque(vec3 position, bool saveHit, inout HitInfo hInfo) {
    float field = getDistancePlaneXZ(position, 0.0);

    if (field < DISTANCE_EPSILON && saveHit) {
        hInfo.type = TYPE_OPAQUE;
        hInfo.material.color = mix(vec3(0.0, 0.0, 0.0), vec3(0.20, 0.0, 1.0), getPatternWeight(position));
        hInfo.material.shininess = 1.0;
        hInfo.material.opacity = 1.0;
        hInfo.material.emissive = vec3(0.0);
        hInfo.material.textAlpha = 0.0;
    }
    return field;
}

float getDistanceSceneTransparent(vec3 position, bool saveHit, inout HitInfo hInfo, float clock_val, vec3 target_val) {
    vec3 single_box_radius = vec3(1.0, 3.0, 1.0);
    float effective_block_size = 4.0;
    float offset_to_center_in_black_square = 1.0;

    vec2 grid_coord_xz = floor(position.xz / effective_block_size);
    vec2 camera_grid_center = floor(u_cameraPosition.xz / effective_block_size);

    vec3 local_position = vec3(
        mod(position.x - offset_to_center_in_black_square + 0.5 * effective_block_size, effective_block_size) - 0.5 * effective_block_size,
        position.y - single_box_radius.y,
        mod(position.z - offset_to_center_in_black_square + 0.5 * effective_block_size, effective_block_size) - 0.5 * effective_block_size
    );

    float field = getDistanceBox(local_position, single_box_radius, 0.01);
    vec2 relative_grid = abs(grid_coord_xz - camera_grid_center);

    if (relative_grid.x < 50.0 && relative_grid.y < 50.0 && field < DISTANCE_EPSILON && saveHit) {
        hInfo.type = TYPE_TRANSPARENT;
        hInfo.material.color = vec3(1.0, 1.0, 1.0);
        hInfo.material.shininess = 32.0;
        hInfo.material.opacity = 0.02;
        hInfo.material.emissive = vec3(0.0);
        hInfo.material.textAlpha = 0.5;

        float chars_per_face_x = 40.0;
        float chars_per_face_y = 80.0;
        vec3 abs_local_pos = abs(local_position);
        vec2 face_uv;
        bool is_valid_face = false;
        float face_epsilon = 0.015;

        if (abs_local_pos.x > single_box_radius.x - face_epsilon && local_position.x > 0.0) {
            face_uv = vec2(1.0 - (local_position.z + single_box_radius.z) / (2.0 * single_box_radius.z),
                          (local_position.y + single_box_radius.y) / (2.0 * single_box_radius.y));
            is_valid_face = true;
        } else if (abs_local_pos.x > single_box_radius.x - face_epsilon && local_position.x < 0.0) {
            face_uv = vec2((local_position.z + single_box_radius.z) / (2.0 * single_box_radius.z),
                          (local_position.y + single_box_radius.y) / (2.0 * single_box_radius.y));
            is_valid_face = true;
        } else if (abs_local_pos.z > single_box_radius.z - face_epsilon && local_position.z > 0.0) {
            face_uv = vec2((local_position.x + single_box_radius.x) / (2.0 * single_box_radius.x),
                          (local_position.y + single_box_radius.y) / (2.0 * single_box_radius.y));
            is_valid_face = true;
        } else if (abs_local_pos.z > single_box_radius.z - face_epsilon && local_position.z < 0.0) {
            face_uv = vec2(1.0 - (local_position.x + single_box_radius.x) / (2.0 * single_box_radius.x),
                          (local_position.y + single_box_radius.y) / (2.0 * single_box_radius.y));
            is_valid_face = true;
        }

        if (is_valid_face) {
            const float marginX = 0.1;
            const float marginY = 0.03;
            vec2 face_margin_min = vec2(marginX, marginY);
            vec2 face_margin_max = vec2(1.0 - marginX, 1.0 - marginY);
            vec2 inner_face_uv = (face_uv - face_margin_min) / (face_margin_max - face_margin_min);

            if (all(greaterThanEqual(inner_face_uv, vec2(0.0))) && all(lessThanEqual(inner_face_uv, vec2(1.0)))) {
                vec2 repeated_uv = inner_face_uv * vec2(chars_per_face_x, chars_per_face_y);
                vec2 char_cell = floor(repeated_uv);
                vec2 char_uv_normalized = fract(repeated_uv);

                int base_building_char = int(mod(grid_coord_xz.x * 7.0 + grid_coord_xz.y * 3.0, 16.0));
                int char_cell_offset = int(mod(char_cell.x * 5.0 + char_cell.y * 7.0, 16.0));
                int char_index = int(mod(float(base_building_char + char_cell_offset), 16.0));

                vec2 char_center = vec2(0.5);
                vec2 char_size_scale = vec2(0.8);
                vec2 char_offset = (char_uv_normalized - char_center) / char_size_scale + 0.5;

                if (all(greaterThanEqual(char_offset, vec2(0.0))) && all(lessThanEqual(char_offset, vec2(1.0)))) {
                    float char_coverage = drawCharacter(char_index, char_offset);
                    hInfo.material.textAlpha = char_coverage;
                    hInfo.material.emissive = vec3(0.0, 1.0, 1.0) * char_coverage;
                }
            }
        }
    }
    return field;
}

float getDistanceScene(vec3 position, bool saveHit, inout HitInfo hInfo, float clock_val, vec3 target_val) {
    if (saveHit) hInfo.type = TYPE_NONE;
    return min(getDistanceSceneOpaque(position, saveHit, hInfo),
               getDistanceSceneTransparent(position, saveHit, hInfo, clock_val, target_val));
}

vec3 getNormalTransparent(vec3 position, float clock_val, vec3 target_val) {
    HitInfo temp;
    vec3 nDelta = vec3(
        getDistanceSceneTransparent(position - vec3(NORMAL_DELTA, 0.0, 0.0), false, temp, clock_val, target_val),
        getDistanceSceneTransparent(position - vec3(0.0, NORMAL_DELTA, 0.0), false, temp, clock_val, target_val),
        getDistanceSceneTransparent(position - vec3(0.0, 0.0, NORMAL_DELTA), false, temp, clock_val, target_val)
    );
    vec3 pDelta = vec3(
        getDistanceSceneTransparent(position + vec3(NORMAL_DELTA, 0.0, 0.0), false, temp, clock_val, target_val),
        getDistanceSceneTransparent(position + vec3(0.0, NORMAL_DELTA, 0.0), false, temp, clock_val, target_val),
        getDistanceSceneTransparent(position + vec3(0.0, 0.0, NORMAL_DELTA), false, temp, clock_val, target_val)
    );
    return normalize(pDelta - nDelta);
}

vec3 getNormalOpaque(vec3 position) {
    HitInfo temp;
    vec3 nDelta = vec3(
        getDistanceSceneOpaque(position - vec3(NORMAL_DELTA, 0.0, 0.0), false, temp),
        getDistanceSceneOpaque(position - vec3(0.0, NORMAL_DELTA, 0.0), false, temp),
        getDistanceSceneOpaque(position - vec3(0.0, 0.0, NORMAL_DELTA), false, temp)
    );
    vec3 pDelta = vec3(
        getDistanceSceneOpaque(position + vec3(NORMAL_DELTA, 0.0, 0.0), false, temp),
        getDistanceSceneOpaque(position + vec3(0.0, NORMAL_DELTA, 0.0), false, temp),
        getDistanceSceneOpaque(position + vec3(0.0, 0.0, NORMAL_DELTA), false, temp)
    );
    return normalize(pDelta - nDelta);
}

float getSoftShadow(vec3 position, vec3 normal, vec3 target_val, float clock_val) {
    position += DISTANCE_EPSILON * normal;
    float delta = 1.0;
    float minimum = 1.0;
    vec3 lightNormal = normalize(vec3(0.0, 20.0, 0.0) - position);

    for (int i = 0; i < ITERATION_COUNT; i++) {
        HitInfo temp;
        float field = max(0.0, getDistanceSceneTransparent(position, false, temp, clock_val, target_val));
        if (field < DISTANCE_EPSILON) return 0.3;
        minimum = min(minimum, 8.0 * field / delta);
        delta += 0.1 * field;
        position += field * 0.25 * lightNormal;
    }
    return clamp(minimum, 0.3, 1.0);
}

float getAmbientOcclusion(vec3 position, vec3 normal, float clock_val, vec3 target_val) {
    HitInfo temp;
    float brightness = getDistanceScene(position + 0.5 * OCCLUSION_DELTA * normal, false, temp, clock_val, target_val);
    brightness += getDistanceScene(position + 2.0 * OCCLUSION_DELTA * normal, false, temp, clock_val, target_val);
    brightness += getDistanceScene(position + 4.0 * OCCLUSION_DELTA * normal, false, temp, clock_val, target_val);
    return clamp(pow(max(0.0, brightness + 0.01), 0.5), 0.5, 1.0);
}

vec3 getLightDiffuse(vec3 surfaceNormal, vec3 lightNormal, vec3 lightColor) {
    return max(0.0, dot(surfaceNormal, lightNormal)) * lightColor;
}

vec3 getLightSpecular(vec3 reflectionNormal, vec3 viewNormal, vec3 lightColor, float power) {
    return pow(max(0.0, dot(reflectionNormal, viewNormal)), power) * lightColor;
}

float getFresnelSchlick(vec3 surfaceNormal, vec3 halfWayNormal, float r0) {
    return r0 + (1.0 - r0) * pow(1.0 - dot(surfaceNormal, halfWayNormal), 5.0);
}

vec3 computeLight(vec3 position, vec3 surfaceNormal, Material material, vec3 viewer_val, vec3 target_val, float clock_val) {
    vec3 lightVector = u_cameraPosition - position;
    float attenuation = 1.0 / dot(lightVector, lightVector);

    if (dot(surfaceNormal, lightVector) <= 0.0) return vec3(0.2) * material.color;

    vec3 lightNormal = normalize(lightVector);
    vec3 lightColor = 1000.0 * vec3(1.0);

    if (material.opacity == 1.0) lightColor *= getSoftShadow(position, lightNormal, target_val, clock_val);

    vec3 viewNormal = normalize(viewer_val - position);
    vec3 halfWayNormal = normalize(viewNormal + lightNormal);
    vec3 reflectionNormal = mirrorRay(lightNormal, surfaceNormal);

    float fresnelTerm = getFresnelSchlick(surfaceNormal, halfWayNormal, material.shininess);
    vec3 lightDiffuse = getLightDiffuse(surfaceNormal, lightNormal, lightColor);
    vec3 lightSpecular = getLightSpecular(reflectionNormal, viewNormal, lightColor, 512.0);
    float brightness = getAmbientOcclusion(position, surfaceNormal, clock_val, target_val);

    return brightness * (vec3(0.2) + attenuation * (material.opacity * lightDiffuse + fresnelTerm * lightSpecular)) * material.color;
}

vec3 getSkyColor(vec3 ray_dir) {
    return mix(vec3(0.05, 0.0, 0.1), vec3(0.0), (ray_dir.y + 1.0) * 0.9);
}

vec3 traceRay(vec3 position, vec3 normal, vec3 viewer_val, vec3 target_val, float clock_val) {
    vec3 colorOutput = vec3(0.0);
    vec3 rayColor = vec3(1.0);
    int mode = MODE_OUTSIDE;

    for(int hitCount = 0; hitCount < MAX_HIT_COUNT; hitCount++) {
        hitInfo.type = TYPE_NONE;
        hitInfo.material.color = vec3(0.0);
        hitInfo.material.emissive = vec3(0.0);
        hitInfo.material.textAlpha = 0.0;

        for (int it = 0; it < ITERATION_COUNT; it++) {
            float field = (mode == MODE_OUTSIDE) ?
                getDistanceScene(position, true, hitInfo, clock_val, target_val) :
                getDistanceSceneTransparent(position, true, hitInfo, clock_val, target_val);

            if ((mode == MODE_OUTSIDE && field < DISTANCE_EPSILON) ||
                (mode == MODE_INSIDE && field > DISTANCE_EPSILON)) break;

            position += STEP_SCALE * max(DISTANCE_EPSILON, abs(field)) * normal;
        }

        if (hitInfo.type == TYPE_OPAQUE) {
            colorOutput += rayColor * computeLight(position, getNormalOpaque(position), hitInfo.material, viewer_val, target_val, clock_val);
            break;
        }
        else if (hitInfo.type == TYPE_TRANSPARENT) {
            vec3 surfaceNormal = getNormalTransparent(position, clock_val, target_val);
            if (mode == MODE_INSIDE) surfaceNormal = -surfaceNormal;

            colorOutput += rayColor * hitInfo.material.emissive;

            vec3 glass_transmission = hitInfo.material.color * (1.0 - hitInfo.material.opacity);
            vec3 text_occlusion = vec3(0.1);
            rayColor *= mix(glass_transmission, text_occlusion, hitInfo.material.textAlpha);

            normal = refract(normal, surfaceNormal, N1 / N2);
            mode = (mode == MODE_INSIDE) ?
                ((dot(normal, surfaceNormal) < 0.0) ? MODE_OUTSIDE : MODE_INSIDE) :
                MODE_INSIDE;
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
    vec3 normal = createRayNormal(
        u_cameraPosition,
        u_cameraTarget,
        u_cameraUp,
        (gl_FragCoord.xy - 0.5 * iResolution.xy) / iResolution.y,
        1.6,
        vec3(0.0)
    );

    vec3 color = traceRay(u_cameraPosition, normal, u_cameraPosition, vec3(0.0, 0.5, 0.0), 0.0);
    FragColor = vec4(pow(color, vec3(1.0 / 2.2)), 1.0);
}