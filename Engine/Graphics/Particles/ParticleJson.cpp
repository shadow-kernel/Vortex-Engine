// Particle descriptors from JSON (.vfx files and the C ABI's *Json entry points).
// A small DOM parser (RFC 8259 subset: objects, arrays, numbers, strings with escapes, true/false/null) with a
// nesting limit, plus the mapping onto EmitterDesc / BeamDesc. Unknown keys are ignored, missing keys keep their
// defaults, so old files keep loading when the schema grows. Keys compare case-insensitively.
#include "ParticleSystem.h"
#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <initializer_list>

namespace vortex::particles
{
	namespace json
	{
		struct Value
		{
			enum Type : u8 { Null, Bool, Number, String, Array, Object } type{ Null };
			bool b{ false };
			double num{ 0.0 };
			std::string str;
			std::vector<Value> arr;
			std::vector<std::pair<std::string, Value>> obj;

			const Value* get(const char* key) const
			{
				if (type != Object) return nullptr;
				for (const auto& kv : obj)
				{
					const std::string& k = kv.first;
					size_t n = std::strlen(key);
					if (k.size() != n) continue;
					bool eq = true;
					for (size_t i = 0; i < n && eq; ++i)
						eq = std::tolower((unsigned char)k[i]) == std::tolower((unsigned char)key[i]);
					if (eq) return &kv.second;
				}
				return nullptr;
			}
		};

		class Parser
		{
		public:
			explicit Parser(const char* s) : p(s) {}
			bool parse(Value& out, std::string* err)
			{
				skip();
				if (!value(out, 0)) { if (err) *err = error.empty() ? "invalid JSON" : error; return false; }
				skip();
				if (*p != 0) { if (err) *err = "trailing characters after the JSON value"; return false; }
				return true;
			}
		private:
			const char* p;
			std::string error;
			static constexpr int MAX_DEPTH = 64;

			void skip()
			{
				for (;;)
				{
					while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') ++p;
					// tolerate // and /* */ comments (hand-edited .vfx files)
					if (p[0] == '/' && p[1] == '/') { while (*p && *p != '\n') ++p; continue; }
					if (p[0] == '/' && p[1] == '*') { p += 2; while (*p && !(p[0] == '*' && p[1] == '/')) ++p; if (*p) p += 2; continue; }
					break;
				}
			}
			bool fail(const char* msg) { if (error.empty()) error = msg; return false; }

			bool value(Value& v, int depth)
			{
				if (depth > MAX_DEPTH) return fail("JSON nested too deeply");
				switch (*p)
				{
				case '{': return object(v, depth);
				case '[': return array(v, depth);
				case '"': v.type = Value::String; return string(v.str);
				case 't': if (std::strncmp(p, "true", 4) == 0) { p += 4; v.type = Value::Bool; v.b = true; return true; } return fail("unexpected token");
				case 'f': if (std::strncmp(p, "false", 5) == 0) { p += 5; v.type = Value::Bool; v.b = false; return true; } return fail("unexpected token");
				case 'n': if (std::strncmp(p, "null", 4) == 0) { p += 4; v.type = Value::Null; return true; } return fail("unexpected token");
				default: return number(v);
				}
			}

			bool number(Value& v)
			{
				const char* s = p;
				if (*p == '-' || *p == '+') ++p;
				if (!std::isdigit((unsigned char)*p) && *p != '.') return fail("expected a value");
				while (std::isdigit((unsigned char)*p)) ++p;
				if (*p == '.') { ++p; while (std::isdigit((unsigned char)*p)) ++p; }
				if (*p == 'e' || *p == 'E') { ++p; if (*p == '-' || *p == '+') ++p; while (std::isdigit((unsigned char)*p)) ++p; }
				std::string tmp(s, p);
				char* end = nullptr;
				v.type = Value::Number;
				v.num = std::strtod(tmp.c_str(), &end);   // strtod is locale-dependent for ',' only; tmp never holds one
				if (!std::isfinite(v.num)) v.num = 0.0;
				return true;
			}

			static void utf8(std::string& out, u32 cp)
			{
				if (cp < 0x80) out.push_back((char)cp);
				else if (cp < 0x800) { out.push_back((char)(0xC0 | (cp >> 6))); out.push_back((char)(0x80 | (cp & 0x3F))); }
				else if (cp < 0x10000) { out.push_back((char)(0xE0 | (cp >> 12))); out.push_back((char)(0x80 | ((cp >> 6) & 0x3F))); out.push_back((char)(0x80 | (cp & 0x3F))); }
				else { out.push_back((char)(0xF0 | (cp >> 18))); out.push_back((char)(0x80 | ((cp >> 12) & 0x3F))); out.push_back((char)(0x80 | ((cp >> 6) & 0x3F))); out.push_back((char)(0x80 | (cp & 0x3F))); }
			}

			bool hex4(u32& cp)
			{
				cp = 0;
				for (int i = 0; i < 4; ++i)
				{
					char c = *p++;
					cp <<= 4;
					if (c >= '0' && c <= '9') cp |= (u32)(c - '0');
					else if (c >= 'a' && c <= 'f') cp |= (u32)(c - 'a' + 10);
					else if (c >= 'A' && c <= 'F') cp |= (u32)(c - 'A' + 10);
					else return fail("bad \\u escape");
				}
				return true;
			}

			bool string(std::string& out)
			{
				++p;   // opening quote
				out.clear();
				while (*p && *p != '"')
				{
					if (*p == '\\')
					{
						++p;
						switch (*p)
						{
						case '"': out.push_back('"'); ++p; break;
						case '\\': out.push_back('\\'); ++p; break;
						case '/': out.push_back('/'); ++p; break;
						case 'b': out.push_back('\b'); ++p; break;
						case 'f': out.push_back('\f'); ++p; break;
						case 'n': out.push_back('\n'); ++p; break;
						case 'r': out.push_back('\r'); ++p; break;
						case 't': out.push_back('\t'); ++p; break;
						case 'u':
						{
							++p;
							u32 cp = 0;
							if (!hex4(cp)) return false;
							if (cp >= 0xD800 && cp <= 0xDBFF && p[0] == '\\' && p[1] == 'u')
							{
								p += 2;
								u32 lo = 0;
								if (!hex4(lo)) return false;
								if (lo >= 0xDC00 && lo <= 0xDFFF) cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
							}
							utf8(out, cp);
							break;
						}
						default: return fail("bad escape");
						}
					}
					else out.push_back(*p++);
				}
				if (*p != '"') return fail("unterminated string");
				++p;
				return true;
			}

			bool array(Value& v, int depth)
			{
				++p;
				v.type = Value::Array;
				skip();
				if (*p == ']') { ++p; return true; }
				for (;;)
				{
					v.arr.emplace_back();
					skip();
					if (!value(v.arr.back(), depth + 1)) return false;
					skip();
					if (*p == ',') { ++p; skip(); if (*p == ']') { ++p; return true; } continue; }   // tolerate a trailing comma
					if (*p == ']') { ++p; return true; }
					return fail("expected ',' or ']'");
				}
			}

			bool object(Value& v, int depth)
			{
				++p;
				v.type = Value::Object;
				skip();
				if (*p == '}') { ++p; return true; }
				for (;;)
				{
					skip();
					if (*p != '"') return fail("expected a key");
					std::string key;
					if (!string(key)) return false;
					skip();
					if (*p != ':') return fail("expected ':'");
					++p;
					skip();
					v.obj.emplace_back(std::move(key), Value{});
					if (!value(v.obj.back().second, depth + 1)) return false;
					skip();
					if (*p == ',') { ++p; skip(); if (*p == '}') { ++p; return true; } continue; }
					if (*p == '}') { ++p; return true; }
					return fail("expected ',' or '}'");
				}
			}
		};

		bool parse(const char* text, Value& out, std::string* err)
		{
			if (!text) { if (err) *err = "null JSON"; return false; }
			Parser ps(text);
			return ps.parse(out, err);
		}
	}

	namespace
	{
		using json::Value;

		float num(const Value* v, float def)
		{
			if (!v) return def;
			if (v->type == Value::Number) return (float)v->num;
			if (v->type == Value::Bool) return v->b ? 1.0f : 0.0f;
			if (v->type == Value::Array && !v->arr.empty() && v->arr[0].type == Value::Number) return (float)v->arr[0].num;
			return def;
		}
		void read(const Value& o, const char* key, float& f) { f = num(o.get(key), f); }
		void read_u(const Value& o, const char* key, u32& u, u32 lo, u32 hi)
		{
			const Value* v = o.get(key);
			if (!v || (v->type != Value::Number && v->type != Value::Bool)) return;
			double d = v->type == Value::Bool ? (v->b ? 1.0 : 0.0) : v->num;
			if (d < (double)lo) d = (double)lo;
			if (d > (double)hi) d = (double)hi;
			u = (u32)d;
		}
		void read_b(const Value& o, const char* key, u32& flag)
		{
			const Value* v = o.get(key);
			if (!v) return;
			if (v->type == Value::Bool) flag = v->b ? 1u : 0u;
			else if (v->type == Value::Number) flag = v->num != 0.0 ? 1u : 0u;
		}
		// [min, max] or a single number (both = it)
		void read_range(const Value& o, const char* key, float* r)
		{
			const Value* v = o.get(key);
			if (!v) return;
			if (v->type == Value::Number) { r[0] = r[1] = (float)v->num; return; }
			if (v->type == Value::Array && !v->arr.empty())
			{
				r[0] = num(&v->arr[0], r[0]);
				r[1] = v->arr.size() > 1 ? num(&v->arr[1], r[0]) : r[0];
			}
			else if (v->type == Value::Object)
			{
				r[0] = num(v->get("min"), r[0]);
				r[1] = num(v->get("max"), r[1]);
			}
		}
		void read_vec(const Value& o, const char* key, float* out, int n)
		{
			const Value* v = o.get(key);
			if (!v) return;
			if (v->type == Value::Array) { for (int i = 0; i < n && i < (int)v->arr.size(); ++i) out[i] = num(&v->arr[i], out[i]); }
			else if (v->type == Value::Object)
			{
				static const char* names[4] = { "x", "y", "z", "w" };
				static const char* cnames[4] = { "r", "g", "b", "a" };
				for (int i = 0; i < n; ++i) { out[i] = num(v->get(names[i]), out[i]); out[i] = num(v->get(cnames[i]), out[i]); }
			}
			else if (v->type == Value::Number) { for (int i = 0; i < n; ++i) out[i] = (float)v->num; }
		}
		std::string str(const Value& o, const char* key)
		{
			const Value* v = o.get(key);
			return v && v->type == Value::String ? v->str : std::string();
		}
		bool ieq(const std::string& a, const char* b)
		{
			size_t n = std::strlen(b);
			if (a.size() != n) return false;
			for (size_t i = 0; i < n; ++i) if (std::tolower((unsigned char)a[i]) != std::tolower((unsigned char)b[i])) return false;
			return true;
		}
		// enum by name (index in `names`) or by number
		void read_enum(const Value& o, const char* key, u32& out, std::initializer_list<const char*> names)
		{
			const Value* v = o.get(key);
			if (!v) return;
			if (v->type == Value::Number) { if (v->num >= 0 && v->num < (double)names.size()) out = (u32)v->num; return; }
			if (v->type != Value::String) return;
			u32 i = 0;
			for (const char* n : names) { if (ieq(v->str, n)) { out = i; return; } ++i; }
		}

		void read_curve(const Value& o, const char* key, Curve& c)
		{
			const Value* v = o.get(key);
			if (!v) return;
			if (v->type == Value::Number) { c = curve_constant((float)v->num); return; }
			const Value* keys = v->type == Value::Object ? v->get("keys") : v;
			if (!keys || keys->type != Value::Array) return;
			Curve r{};
			for (const Value& k : keys->arr)
			{
				if (r.count >= MAX_CURVE_KEYS) break;
				float t = 0.0f, val = 1.0f;
				if (k.type == Value::Object) { t = num(k.get("t"), num(k.get("time"), 0.0f)); val = num(k.get("v"), num(k.get("value"), 1.0f)); }
				else if (k.type == Value::Array && k.arr.size() >= 2) { t = num(&k.arr[0], 0.0f); val = num(&k.arr[1], 1.0f); }
				else continue;
				r.t[r.count] = std::min(1.0f, std::max(0.0f, t));
				r.v[r.count] = val;
				++r.count;
			}
			// keys must ascend in t (the editor keeps them sorted; hand-written files may not)
			for (u32 i = 1; i < r.count; ++i)
				for (u32 j = i; j > 0 && r.t[j] < r.t[j - 1]; --j) { std::swap(r.t[j], r.t[j - 1]); std::swap(r.v[j], r.v[j - 1]); }
			c = r;
		}

		void read_gradient(const Value& o, const char* key, Gradient& g)
		{
			const Value* v = o.get(key);
			if (!v) return;
			const Value* keys = v->type == Value::Object ? v->get("keys") : v;
			if (!keys || keys->type != Value::Array) return;
			Gradient r{};
			for (const Value& k : keys->arr)
			{
				if (r.count >= MAX_GRADIENT_KEYS) break;
				float t = 0.0f, c[4] = { 1, 1, 1, 1 };
				if (k.type == Value::Object)
				{
					t = num(k.get("t"), num(k.get("time"), 0.0f));
					c[0] = num(k.get("r"), 1.0f); c[1] = num(k.get("g"), 1.0f); c[2] = num(k.get("b"), 1.0f); c[3] = num(k.get("a"), 1.0f);
					if (const Value* col = k.get("color")) { Value tmp; tmp.type = Value::Object; tmp.obj.emplace_back("c", *col); read_vec(tmp, "c", c, 4); }
				}
				else if (k.type == Value::Array && k.arr.size() >= 5)
				{
					t = num(&k.arr[0], 0.0f);
					for (int i = 0; i < 4; ++i) c[i] = num(&k.arr[1 + i], 1.0f);
				}
				else continue;
				r.t[r.count] = std::min(1.0f, std::max(0.0f, t));
				for (int i = 0; i < 4; ++i) r.rgba[r.count][i] = std::max(0.0f, c[i]);
				++r.count;
			}
			for (u32 i = 1; i < r.count; ++i)
				for (u32 j = i; j > 0 && r.t[j] < r.t[j - 1]; --j)
				{
					std::swap(r.t[j], r.t[j - 1]);
					for (int q = 0; q < 4; ++q) std::swap(r.rgba[j][q], r.rgba[j - 1][q]);
				}
			g = r;
		}

		void read_emitter(const Value& o, EmitterJson& e)
		{
			EmitterDesc& d = e.desc;
			e.name = str(o, "name");
			if (const Value* en = o.get("enabled")) e.enabled = en->type == Value::Bool ? en->b : (en->type == Value::Number ? en->num != 0.0 : true);

			read_u(o, "maxParticles", d.max_particles, 1, MAX_PARTICLES_PER_EMITTER);
			read(o, "duration", d.duration);
			read_b(o, "looping", d.looping);
			read_b(o, "prewarm", d.prewarm);
			read(o, "startDelay", d.start_delay);
			read_enum(o, "simulationSpace", d.simulation_space, { "world", "local" });
			read(o, "simulationSpeed", d.simulation_speed);
			read_u(o, "seed", d.seed, 0, 0xFFFFFFFFu);
			read(o, "rate", d.rate);
			read(o, "rateOverDistance", d.rate_over_distance);
			if (const Value* bursts = o.get("bursts"); bursts && bursts->type == Value::Array)
			{
				d.burst_count = 0;
				for (const Value& b : bursts->arr)
				{
					if (b.type != Value::Object || d.burst_count >= MAX_BURSTS) continue;
					Burst& br = d.bursts[d.burst_count++];
					br = Burst{ 0.0f, 10, 10, 1, 0.1f, 1.0f };
					read(b, "time", br.time);
					float cnt[2] = { 10.0f, 10.0f };
					if (const Value* c = b.get("count"); c && c->type == Value::Array) read_range(b, "count", cnt);
					else { cnt[0] = num(b.get("count"), 10.0f); cnt[1] = num(b.get("countMax"), cnt[0]); }
					br.count_min = (u32)std::max(0.0f, std::min(cnt[0], (float)MAX_PARTICLES_PER_EMITTER));
					br.count_max = (u32)std::max((float)br.count_min, std::min(cnt[1], (float)MAX_PARTICLES_PER_EMITTER));
					read_u(b, "cycles", br.cycles, 0, 100000);
					read(b, "interval", br.interval);
					read(b, "probability", br.probability);
				}
			}
			read_range(o, "lifetime", d.lifetime);
			read_range(o, "speed", d.speed);
			read_range(o, "size", d.size);
			read_range(o, "rotation", d.rotation);
			read_range(o, "rotationSpeed", d.rotation_speed);
			read_vec(o, "color", d.color, 4);
			std::memcpy(d.color2, d.color, sizeof(d.color2));
			read_vec(o, "color2", d.color2, 4);
			read(o, "gravity", d.gravity);
			read(o, "drag", d.drag);
			read(o, "inheritVelocity", d.inherit_velocity);

			if (const Value* s = o.get("shape"); s && s->type == Value::Object)
			{
				read_enum(*s, "type", d.shape, { "point", "sphere", "hemisphere", "cone", "box", "circle", "edge" });
				read_enum(*s, "emitFrom", d.emit_from, { "volume", "shell", "edge" });
				read(*s, "radius", d.radius);
				read(*s, "radiusThickness", d.radius_thickness);
				read(*s, "angle", d.angle);
				read(*s, "arc", d.arc);
				read(*s, "length", d.length);
				read_vec(*s, "box", d.box, 3);
				read_vec(*s, "offset", d.shape_offset, 3);
				read_vec(*s, "rotation", d.shape_rotation, 3);
				read(*s, "randomDirection", d.random_direction);
			}
			else if (const Value* s2 = o.get("shape"); s2 && s2->type == Value::String)
				read_enum(o, "shape", d.shape, { "point", "sphere", "hemisphere", "cone", "box", "circle", "edge" });

			read_vec(o, "velocity", d.velocity, 3);
			read_enum(o, "velocitySpace", d.velocity_space, { "world", "local" });
			read_curve(o, "velocityCurve", d.velocity_curve);
			read_curve(o, "speedCurve", d.speed_curve);
			read_curve(o, "sizeCurve", d.size_curve);
			read_gradient(o, "colorGradient", d.color_gradient);

			if (const Value* n = o.get("noise"); n && n->type == Value::Object)
			{
				read(*n, "strength", d.noise_strength);
				read(*n, "frequency", d.noise_frequency);
				read(*n, "scrollSpeed", d.noise_scroll);
				read_u(*n, "octaves", d.noise_octaves, 1, 4);
			}
			if (const Value* f = o.get("flipbook"); f && f->type == Value::Object)
			{
				read_u(*f, "tilesX", d.tiles_x, 1, 64);
				read_u(*f, "tilesY", d.tiles_y, 1, 64);
				read_enum(*f, "mode", d.flipbook_mode, { "lifetime", "fps", "random" });
				read(*f, "cycles", d.flipbook_cycles);
				read(*f, "fps", d.flipbook_fps);
				read_range(*f, "startFrame", d.start_frame);
				read_b(*f, "blend", d.frame_blend);
			}
			if (const Value* r = o.get("render"); r && r->type == Value::Object)
			{
				read_enum(*r, "mode", d.render_mode, { "billboard", "stretched", "horizontal", "vertical" });
				read_enum(*r, "blend", d.blend, { "alpha", "additive", "premultiplied" });
				e.texture = str(*r, "texture");
				read_b(*r, "lit", d.lit);
				read(*r, "softDistance", d.soft_distance);
				read(*r, "lengthScale", d.length_scale);
				read(*r, "velocityScale", d.velocity_scale);
				read_enum(*r, "sort", d.sort_mode, { "auto", "none", "depth" });
				read(*r, "emissive", d.emissive);
				read(*r, "aspect", d.aspect);
				read_u(*r, "layer", d.layer, 0, 1);
			}
			if (const Value* c = o.get("collision"); c && c->type == Value::Object)
			{
				read_b(*c, "enabled", d.collision);
				read_enum(*c, "mode", d.collision_mode, { "bounce", "kill" });
				read(*c, "bounce", d.bounce);
				read(*c, "dampen", d.dampen);
				read(*c, "lifetimeLoss", d.lifetime_loss);
				read(*c, "radius", d.collision_radius);
				read(*c, "thickness", d.collision_thickness);
			}
			if (const Value* t = o.get("trails"); t && t->type == Value::Object)
			{
				read_b(*t, "enabled", d.trails);
				read(*t, "lifetime", d.trail_lifetime);
				read(*t, "minDistance", d.trail_min_distance);
				read_u(*t, "maxPoints", d.trail_max_points, 2, MAX_TRAIL_POINTS);
				read(*t, "width", d.trail_width);
				read_curve(*t, "widthCurve", d.trail_width_curve);
				read_gradient(*t, "colorGradient", d.trail_gradient);
				read_b(*t, "inheritColor", d.trail_inherit_color);
				read_enum(*t, "textureMode", d.trail_texture_mode, { "stretch", "tile" });
				e.trail_texture = str(*t, "texture");
			}
		}

		void read_beam(const Value& o, BeamJson& b)
		{
			BeamDesc& d = b.desc;
			read(o, "width", d.width);
			read_curve(o, "widthCurve", d.width_curve);
			read_gradient(o, "colorGradient", d.gradient);
			read_vec(o, "color", d.color, 4);
			read_enum(o, "blend", d.blend, { "alpha", "additive", "premultiplied" });
			read(o, "emissive", d.emissive);
			read(o, "uvTiling", d.uv_tiling);
			read(o, "uvScroll", d.uv_scroll);
			read_u(o, "segments", d.segments, 1, 256);
			read(o, "noise", d.noise);
			read(o, "noiseFrequency", d.noise_frequency);
			read(o, "noiseSpeed", d.noise_speed);
			read(o, "duration", d.duration);
			read(o, "fadeIn", d.fade_in);
			read(o, "fadeOut", d.fade_out);
			read(o, "speed", d.speed);
			read(o, "length", d.length);
			read(o, "softDistance", d.soft_distance);
			read_u(o, "layer", d.layer, 0, 1);
			b.texture = str(o, "texture");
		}
	}

	bool parse_emitter_json(const char* text, EmitterJson& out, std::string* error)
	{
		Value root;
		if (!json::parse(text, root, error)) return false;
		if (root.type != Value::Object) { if (error) *error = "an emitter must be a JSON object"; return false; }
		default_emitter_desc(out.desc);
		out.name.clear(); out.texture.clear(); out.trail_texture.clear(); out.enabled = true;
		read_emitter(root, out);
		return true;
	}

	bool parse_beam_json(const char* text, BeamJson& out, std::string* error)
	{
		Value root;
		if (!json::parse(text, root, error)) return false;
		if (root.type != Value::Object) { if (error) *error = "a beam must be a JSON object"; return false; }
		default_beam_desc(out.desc);
		out.texture.clear();
		const Value* b = root.get("beam");
		read_beam(b && b->type == Value::Object ? *b : root, out);
		return true;
	}

	bool parse_effect_json(const char* text, std::vector<EmitterJson>& emitters, BeamJson* beam, bool* has_beam, std::string* error)
	{
		emitters.clear();
		if (has_beam) *has_beam = false;
		Value root;
		if (!json::parse(text, root, error)) return false;
		if (root.type != Value::Object) { if (error) *error = "an effect must be a JSON object"; return false; }
		const Value* list = root.get("emitters");
		if (list && list->type == Value::Array)
		{
			for (const Value& ev : list->arr)
			{
				if (ev.type != Value::Object) continue;
				EmitterJson e;
				default_emitter_desc(e.desc);
				read_emitter(ev, e);
				emitters.push_back(std::move(e));
			}
		}
		else if (!root.get("beam"))
		{
			EmitterJson e;   // a bare emitter object
			default_emitter_desc(e.desc);
			read_emitter(root, e);
			emitters.push_back(std::move(e));
		}
		if (const Value* b = root.get("beam"); b && b->type == Value::Object && beam)
		{
			default_beam_desc(beam->desc);
			beam->texture.clear();
			read_beam(*b, *beam);
			if (has_beam) *has_beam = true;
		}
		return true;
	}
}
