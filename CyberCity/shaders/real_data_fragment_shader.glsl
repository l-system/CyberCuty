#version 330 core
out vec4 FragColor;

// === UNIFORMS ===
uniform float iTime;
uniform vec2 iResolution;
uniform vec3 u_cameraPosition;
uniform vec3 u_cameraTarget;
uniform vec3 u_cameraUp;
uniform float N2;
uniform sampler2DArray u_directoryTexturesArray;
uniform int u_buildingTextureCount;

// === CONSTANTS ===
// Ray marching
#define DISTANCE_EPSILON 0.001
#define NORMAL_DELTA 0.0001
#define OCCLUSION_DELTA 0.1
#define STEP_SCALE 1.0
#define MAX_HIT_COUNT 3
#define ITERATION_COUNT 1000
#define REFLECTION_ITERATION_COUNT 100
#define TEXTURE_DISTANCE 100
#define PI 3.14159265359

// Material types
#define TYPE_NONE 0
#define TYPE_OPAQUE 1
#define TYPE_TRANSPARENT 2

// Ray marching modes
#define MODE_OUTSIDE 0
#define MODE_INSIDE 1

// Refraction indices
#define N1 1.0
#define FLOOR_IOR 1.0

// Reflection settings
#define FLOOR_REFLECTION_BASE 1.0
#define FLOOR_REFLECTION_HEIGHT_FALLOFF 1.0
#define FLOOR_REFLECTION_MIN_HEIGHT 0.1
#define FLOOR_REFLECTION_MAX_HEIGHT 5.0
#define REFLECTION_MAX_DISTANCE 100.0
#define REFLECTION_FADE_START 10.0

// Fresnel reflection coefficient macro
#define R0(x, y) (((x - y) / (x + y)) * ((x - y) / (x + y)))

// === DATA STRUCTURES ===
struct Material {
    vec3 color;
    float shininess;
    float opacity;
    vec3 emissive;
    float textAlpha;
    bool isFloor;
};

struct HitInfo {
    Material material;
    int type;
};

HitInfo hitInfo;

// === UTILITY FUNCTIONS ===
vec3 mirrorRay(vec3 ray, vec3 normal) {
    return ray - 2.0 * dot(ray, normal) * normal;
}

mat2 getRotation(float r) {
    return mat2(cos(r), sin(r), -sin(r), cos(r));
}

float median(float r, float g, float b) {
    return max(min(r, g), min(max(r, g), b));
}

// === PATTERN GENERATION ===
float getPatternWeight(vec3 position) {
    float gridLineWidth = 0.0125;
    vec2 fracXZ = fract(position.xz / 2.0);
    float isGridLine = 0.0;

    if (fracXZ.x < gridLineWidth || fracXZ.x > (1.0 - gridLineWidth)) {
        isGridLine = 1.0;
    }
    if (fracXZ.y < gridLineWidth || fracXZ.y > (1.0 - gridLineWidth)) {
        isGridLine = 1.0;
    }

    return isGridLine;
}

float hash21(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

int getTextureIndexForBuilding(vec2 gridCoord, int textureCount) {
    // Generate pseudo-random value based on grid position
    float h = hash21(gridCoord);

    // Map to texture index range
    int index = int(floor(h * float(textureCount)));

    // Ensure valid range
    return clamp(index, 0, textureCount - 1);
}

// === DISTANCE FIELD FUNCTIONS ===
float getDistanceBox(vec3 position, vec3 boxRadius, float roundRadius) {
    vec3 delta = abs(position) - boxRadius;
    return min(max(delta.x, max(delta.y, delta.z)), 0.0) + length(max(delta, 0.0)) - roundRadius;
}

float getDistancePlaneXZ(vec3 position, float height) {
    return position.y - height;
}

// === TEXTURE MAPPING ===
bool getFaceUVPrecise(vec3 localPosition, vec3 boxRadius, out vec2 faceUV, out vec3 faceNormal, out float faceConfidence) {
    vec3 absLocalPos = abs(localPosition);
    float faceEpsilon = 0.95;

    // Calculate distance to each face
    float distX = abs(absLocalPos.x - boxRadius.x);
    float distY = abs(absLocalPos.y - boxRadius.y);
    float distZ = abs(absLocalPos.z - boxRadius.z);

    float minDist = min(distX, min(distY, distZ));
    faceConfidence = 1.0 - (minDist / faceEpsilon);

    if (minDist > faceEpsilon) {
        return false;
    }

    if (distX == minDist) {
        // X face
        faceNormal = vec3(sign(localPosition.x), 0.0, 0.0);
        if (localPosition.x > 0.0) {
            faceUV = vec2(1.0 - (localPosition.z + boxRadius.z) / (2.0 * boxRadius.z),
                         (localPosition.y + boxRadius.y) / (2.0 * boxRadius.y));
        } else {
            faceUV = vec2((localPosition.z + boxRadius.z) / (2.0 * boxRadius.z),
                         (localPosition.y + boxRadius.y) / (2.0 * boxRadius.y));
        }
        return true;
    } else if (distZ == minDist) {
        // Z face
        faceNormal = vec3(0.0, 0.0, sign(localPosition.z));
        if (localPosition.z > 0.0) {
            faceUV = vec2((localPosition.x + boxRadius.x) / (2.0 * boxRadius.x),
                         (localPosition.y + boxRadius.y) / (2.0 * boxRadius.y));
        } else {
            faceUV = vec2(1.0 - (localPosition.x + boxRadius.x) / (2.0 * boxRadius.x),
                         (localPosition.y + boxRadius.y) / (2.0 * boxRadius.y));
        }
        return true;
    }

    return false;
}

// === SCENE DISTANCE FUNCTIONS ===
float getDistanceSceneOpaque(vec3 position, bool saveHit, inout HitInfo hInfo) {
    float field = getDistancePlaneXZ(position, 0.0);

    if (field < DISTANCE_EPSILON && saveHit) {
        hInfo.type = TYPE_OPAQUE;
        hInfo.material.color = mix(vec3(0.0, 0.0, 0.0), vec3(0.20, 0.0, 1.0), getPatternWeight(position));
        hInfo.material.shininess = 8.0;
        hInfo.material.opacity = 1.0;
        hInfo.material.emissive = vec3(0.0);
        hInfo.material.textAlpha = 0.0;
        hInfo.material.isFloor = true;
    }

    return field;
}

float getDistanceSceneTransparent(vec3 position, bool saveHit, inout HitInfo hInfo) {
    vec3 boxRadius = vec3(0.98, 3.0, 0.98);
    float blockSize = 6.0;
    float offsetToCenter = 1.0;

    vec2 gridCoordXZ = floor(position.xz / blockSize);
    vec2 cameraGridCenter = floor(u_cameraPosition.xz / blockSize);

    vec3 localPosition = vec3(
        mod(position.x - offsetToCenter + 0.5 * blockSize, blockSize) - 0.5 * blockSize,
        position.y - boxRadius.y,
        mod(position.z - offsetToCenter + 0.5 * blockSize, blockSize) - 0.5 * blockSize
    );

    float field = getDistanceBox(localPosition, boxRadius, 0.01);
    vec2 relativeGrid = abs(gridCoordXZ - cameraGridCenter);

    if (relativeGrid.x < TEXTURE_DISTANCE && relativeGrid.y < TEXTURE_DISTANCE &&
        field < DISTANCE_EPSILON && saveHit) {

        hInfo.type = TYPE_TRANSPARENT;
        hInfo.material.color = vec3(0.1, 0.3, 0.4);
        hInfo.material.shininess = 32.0;
        hInfo.material.opacity = 0.2;
        hInfo.material.emissive = vec3(0.0);
        hInfo.material.textAlpha = 0.0;
        hInfo.material.isFloor = false;

        int buildingIndex = getTextureIndexForBuilding(gridCoordXZ, u_buildingTextureCount);

        if (u_buildingTextureCount > 0 && buildingIndex < u_buildingTextureCount) {
            vec2 faceUV;
            vec3 faceNormal;
            float faceConfidence;

            if (getFaceUVPrecise(localPosition, boxRadius, faceUV, faceNormal, faceConfidence)) {
                const float marginX = -0.10;
                const float marginY = -0.2;
                vec2 faceMarginMin = vec2(marginX, marginY);
                vec2 faceMarginMax = vec2(1.4 - marginX, 1.4 - marginY);

                vec2 innerFaceUV = (faceUV - faceMarginMin) / (faceMarginMax - faceMarginMin);

                if (all(greaterThanEqual(innerFaceUV, vec2(0.0))) && all(lessThanEqual(innerFaceUV, vec2(1.0)))) {
                    vec2 textScale = vec2(2.0);
                    vec2 textCenterOffset = vec2(0.25);
                    vec2 scaledUV = innerFaceUV * textScale - textCenterOffset;
                    vec2 boundarySoftness = vec2(0.02);
                    vec2 boundaryFactor = smoothstep(vec2(0.0) - boundarySoftness, vec2(0.0), scaledUV) *
                                         (1.0 - smoothstep(vec2(1.0), vec2(1.0) + boundarySoftness, scaledUV));
                    float boundaryWeight = boundaryFactor.x * boundaryFactor.y;

                    if (all(greaterThanEqual(scaledUV, vec2(-0.02))) && all(lessThanEqual(scaledUV, vec2(1.02)))) {
                        vec4 textColorSample = texture(u_directoryTexturesArray, vec3(scaledUV, float(buildingIndex)));
                        float textAlpha = textColorSample.a;

                        if (textAlpha > 0.05) {
                            textAlpha = smoothstep(0.15, 0.85, textAlpha);
                            textAlpha = pow(textAlpha, 0.75);

                            float distanceToCamera = length(position - u_cameraPosition);
                            float distanceFactor = smoothstep(5.0, 25.0, distanceToCamera);
                            textAlpha *= mix(1.0, 0.9, distanceFactor);

                            textAlpha *= faceConfidence * boundaryWeight;
                            float textLuminance = dot(textColorSample.rgb, vec3(0.299, 0.587, 0.114));
                            textAlpha *= mix(0.7, 1.0, textLuminance);
                        }

                        hInfo.material.textAlpha = clamp(textAlpha, 0.0, 1.0);
                        vec3 cyanBase = vec3(0.0, 1.3, 1.4);
                        float textLuminance = dot(textColorSample.rgb, vec3(0.299, 0.587, 0.114));
                        float adaptiveBrightness = mix(0.8, 1.2, textLuminance);
                        float sharpTextFactor = textAlpha * adaptiveBrightness;
                        sharpTextFactor = pow(sharpTextFactor, 1.1);

                        float distanceToCamera = length(position - u_cameraPosition);
                        float emissiveIntensity = mix(2.0, 1.2, smoothstep(10.0, 40.0, distanceToCamera));
                        hInfo.material.emissive = cyanBase * sharpTextFactor * emissiveIntensity;
                        vec3 enhancedCyan = cyanBase * 1.1;
                        hInfo.material.color = mix(hInfo.material.color, enhancedCyan, sharpTextFactor * 0.85);
                    }
                }
            }
        }
    }

    return field;
}

float getDistanceScene(vec3 position, bool saveHit, inout HitInfo hInfo) {
    if (saveHit) {
        hInfo.type = TYPE_NONE;
    }

    return min(getDistanceSceneOpaque(position, saveHit, hInfo),
               getDistanceSceneTransparent(position, saveHit, hInfo));
}

// === NORMAL CALCULATION ===
vec3 getNormalTransparent(vec3 position) {
    HitInfo temp;
    vec3 nDelta = vec3(
        getDistanceSceneTransparent(position - vec3(NORMAL_DELTA, 0.0, 0.0), false, temp),
        getDistanceSceneTransparent(position - vec3(0.0, NORMAL_DELTA, 0.0), false, temp),
        getDistanceSceneTransparent(position - vec3(0.0, 0.0, NORMAL_DELTA), false, temp)
    );
    vec3 pDelta = vec3(
        getDistanceSceneTransparent(position + vec3(NORMAL_DELTA, 0.0, 0.0), false, temp),
        getDistanceSceneTransparent(position + vec3(0.0, NORMAL_DELTA, 0.0), false, temp),
        getDistanceSceneTransparent(position + vec3(0.0, 0.0, NORMAL_DELTA), false, temp)
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

// === LIGHTING FUNCTIONS ===
float getSoftShadow(vec3 position, vec3 normal) {
    position += DISTANCE_EPSILON * normal;
    float delta = 1.0;
    float minimum = 1.0;
    vec3 lightNormal = normalize(vec3(0.0, 20.0, 0.0) - position);

    for (int i = 0; i < ITERATION_COUNT; i++) {
        HitInfo temp;
        float field = max(0.0, getDistanceSceneTransparent(position, false, temp));
        if (field < DISTANCE_EPSILON) {
            return 0.3;
        }
        minimum = min(minimum, 8.0 * field / delta);
        delta += 0.1 * field;
        position += field * 0.25 * lightNormal;
    }

    return clamp(minimum, 0.3, 1.0);
}

float getAmbientOcclusion(vec3 position, vec3 normal) {
    HitInfo temp;
    float brightness = getDistanceScene(position + 0.5 * OCCLUSION_DELTA * normal, false, temp);
    brightness += getDistanceScene(position + 2.0 * OCCLUSION_DELTA * normal, false, temp);
    brightness += getDistanceScene(position + 4.0 * OCCLUSION_DELTA * normal, false, temp);
    return clamp(pow(max(0.0, brightness + 0.01), 0.5), 0.5, 1.0);
}

vec3 getLightDiffuse(vec3 surfaceNormal, vec3 lightNormal, vec3 lightColor) {
    return max(0.0, dot(surfaceNormal, lightNormal)) * lightColor;
}

vec3 getLightSpecular(vec3 reflectionNormal, vec3 viewNormal, vec3 lightColor, float power) {
    return pow(max(0.0, dot(reflectionNormal, viewNormal)), power) * lightColor;
}

float getFresnelSchlick(vec3 surfaceNormal, vec3 viewDirection, float ior) {
    float r0 = R0(1.0, ior);
    float cosTheta = abs(dot(surfaceNormal, viewDirection));
    return r0 + (1.0 - r0) * pow(1.0 - cosTheta, 5.0);
}

// === REFLECTION RAY TRACING ===
vec3 traceReflectionRay(vec3 position, vec3 reflectionDir, float maxDistance) {
    vec3 currentPos = position + DISTANCE_EPSILON * reflectionDir;

    for (int i = 0; i < REFLECTION_ITERATION_COUNT; i++) {
        HitInfo tempHit;
        float field = getDistanceScene(currentPos, true, tempHit);

        if (field < DISTANCE_EPSILON) {
            if (tempHit.type == TYPE_OPAQUE) {
                // Hit opaque surface - calculate basic lighting
                vec3 normal = getNormalOpaque(currentPos);
                vec3 lightDir = normalize(u_cameraPosition - currentPos);
                float diffuse = max(0.0, dot(normal, lightDir));
                return tempHit.material.color * (0.3 + 0.7 * diffuse);
            } else if (tempHit.type == TYPE_TRANSPARENT) {
                // Hit transparent surface - return attenuated building color with emissive
                return tempHit.material.color * 0.02 + tempHit.material.emissive * 0.5;
            }
        }

        float stepSize = max(DISTANCE_EPSILON, abs(field));
        currentPos += stepSize * reflectionDir;

        // Distance falloff check
        if (length(currentPos - position) > maxDistance) {
            break;
        }
    }

    // No hit - return sky color
    return mix(vec3(0.05, 0.0, 0.1), vec3(0.0), (reflectionDir.y + 1.0) * 0.9);
}

vec3 computeLight(vec3 position, vec3 surfaceNormal, Material material, vec3 viewerPosition) {
    vec3 lightVector = u_cameraPosition - position;
    float attenuation = 1.0 / dot(lightVector, lightVector);

    if (dot(surfaceNormal, lightVector) <= 0.0) {
        return vec3(0.2) * material.color;
    }

    vec3 lightNormal = normalize(lightVector);
    vec3 lightColor = 1000.0 * vec3(1.0);

    if (material.opacity == 1.0) {
        lightColor *= getSoftShadow(position, lightNormal);
    }

    vec3 viewNormal = normalize(viewerPosition - position);
    vec3 halfWayNormal = normalize(viewNormal + lightNormal);
    vec3 reflectionNormal = mirrorRay(lightNormal, surfaceNormal);

    float fresnelTerm = getFresnelSchlick(surfaceNormal, halfWayNormal, material.shininess);
    vec3 lightDiffuse = getLightDiffuse(surfaceNormal, lightNormal, lightColor);
    vec3 lightSpecular = getLightSpecular(reflectionNormal, viewNormal, lightColor, 512.0);
    float brightness = getAmbientOcclusion(position, surfaceNormal);

    vec3 baseColor = brightness * (vec3(0.2) + attenuation * (material.opacity * lightDiffuse + fresnelTerm * lightSpecular)) * material.color;

    // Add floor reflections
    if (material.isFloor) {
        vec3 viewDir = normalize(viewerPosition - position);
        vec3 reflectionDir = mirrorRay(-viewDir, surfaceNormal);

        // Calculate Fresnel for floor reflection
        float fresnel = getFresnelSchlick(surfaceNormal, viewDir, FLOOR_IOR);

        // Distance-based reflection falloff
        float distanceToCamera = length(position - u_cameraPosition);
        float distanceFalloff = smoothstep(REFLECTION_MAX_DISTANCE, REFLECTION_FADE_START, distanceToCamera);

        // Height-based reflection intensity
        vec3 reflectedObjectPos = position + reflectionDir * 2.0;
        float objectHeight = max(0.0, reflectedObjectPos.y - position.y);

        float heightFalloff = 1.0;
        if (objectHeight > FLOOR_REFLECTION_MIN_HEIGHT) {
            float normalizedHeight = clamp(objectHeight / FLOOR_REFLECTION_MAX_HEIGHT, 0.0, 1.0);
            heightFalloff = exp(-FLOOR_REFLECTION_HEIGHT_FALLOFF * normalizedHeight * 5.0);
        }

        // Trace reflection ray
        vec3 reflectionColor = traceReflectionRay(position, reflectionDir, REFLECTION_MAX_DISTANCE);

        // Combine all falloff factors
        float reflectionStrength = fresnel * distanceFalloff * heightFalloff * FLOOR_REFLECTION_BASE;

        // Add angle-based intensity boost for grazing angles
        float grazingAngle = 1.0 - abs(dot(surfaceNormal, viewDir));
        reflectionStrength *= mix(1.0, 2.0, grazingAngle);
        reflectionStrength += mix(0.02, 0.0, grazingAngle);

        // Clamp to prevent over-bright reflections
        reflectionStrength = clamp(reflectionStrength, 0.0, 0.1);

        // Blend reflection with base color
        baseColor = mix(baseColor, reflectionColor, reflectionStrength);
    }

    return baseColor;
}

vec3 getSkyColor(vec3 rayDir) {
    return mix(vec3(0.05, 0.0, 0.1), vec3(0.0), (rayDir.y + 1.0) * 0.9);
}

// === MAIN RAY TRACING ===
vec3 traceRay(vec3 position, vec3 normal, vec3 viewerPosition) {
    vec3 colorOutput = vec3(0.0);
    vec3 rayColor = vec3(1.0);
    int mode = MODE_OUTSIDE;

    for (int hitCount = 0; hitCount < MAX_HIT_COUNT; hitCount++) {
        hitInfo.type = TYPE_NONE;
        hitInfo.material.color = vec3(0.0);
        hitInfo.material.emissive = vec3(0.0);
        hitInfo.material.textAlpha = 0.0;

        for (int it = 0; it < ITERATION_COUNT; it++) {
            float field = (mode == MODE_OUTSIDE) ?
                getDistanceScene(position, true, hitInfo) :
                getDistanceSceneTransparent(position, true, hitInfo);

            if ((mode == MODE_OUTSIDE && field < DISTANCE_EPSILON) ||
                (mode == MODE_INSIDE && field > DISTANCE_EPSILON)) {
                break;
            }

            position += STEP_SCALE * max(DISTANCE_EPSILON, abs(field)) * normal;
        }

        if (hitInfo.type == TYPE_OPAQUE) {
            colorOutput += rayColor * computeLight(position, getNormalOpaque(position), hitInfo.material, viewerPosition);
            break;
        } else if (hitInfo.type == TYPE_TRANSPARENT) {
            vec3 surfaceNormal = getNormalTransparent(position);
            if (mode == MODE_INSIDE) {
                surfaceNormal = -surfaceNormal;
            }

            colorOutput += rayColor * hitInfo.material.emissive;

            vec3 glassTransmission = hitInfo.material.color * (1.0 - hitInfo.material.opacity);
            vec3 textOcclusion = vec3(0.1);
            rayColor *= mix(glassTransmission, textOcclusion, hitInfo.material.textAlpha);

            normal = refract(normal, surfaceNormal, N1 / N2);
            mode = (mode == MODE_INSIDE) ?
                ((dot(normal, surfaceNormal) < 0.0) ? MODE_OUTSIDE : MODE_INSIDE) :
                MODE_INSIDE;
        } else {
            colorOutput += rayColor * getSkyColor(normal);
            break;
        }
    }

    return colorOutput;
}

// === RAY GENERATION ===
vec3 createRayNormal(vec3 origin, vec3 target, vec3 up, vec2 plane, float fov, vec3 displacement) {
    vec3 axisZ = normalize(target - origin);
    vec3 axisX = normalize(cross(axisZ, up));
    vec3 axisY = cross(axisX, axisZ);
    origin += mat3(axisX, axisY, axisZ) * -displacement;
    vec3 point = target + fov * length(target - origin) * (plane.x * axisX + plane.y * axisY);
    return normalize(point - origin);
}

// === MAIN FUNCTION ===
void main() {
    vec3 normal = createRayNormal(
        u_cameraPosition,
        u_cameraTarget,
        u_cameraUp,
        (gl_FragCoord.xy - 0.5 * iResolution.xy) / iResolution.y,
        1.6,
        vec3(0.0)
    );

    vec3 color = traceRay(u_cameraPosition, normal, u_cameraPosition);
    FragColor = vec4(pow(color, vec3(1.0 / 2.2)), 1.0);
}