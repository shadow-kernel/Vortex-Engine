#version 450
// SSAO 4-tap blur (ssao.metal SsaoBlurPS).
layout(location = 0) in  vec2 v_uv;
layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D u_src;

layout(set = 3, binding = 0, std140) uniform SsaoCB
{
	mat4  inv_proj;
	vec2  texel;
	float radius;
	float intensity;
	float bias;
	float proj_scale;
	vec2  pad;
} c;

void main()
{
	float s = 0.0;
	s += texture(u_src, v_uv + c.texel * vec2(-0.5, -0.5)).r;
	s += texture(u_src, v_uv + c.texel * vec2( 1.5, -0.5)).r;
	s += texture(u_src, v_uv + c.texel * vec2(-0.5,  1.5)).r;
	s += texture(u_src, v_uv + c.texel * vec2( 1.5,  1.5)).r;
	float ao = s * 0.25;
	o_color = vec4(ao, ao, ao, 1.0);
}
