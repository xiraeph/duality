﻿using System;
using System.Collections.Generic;
using System.Linq;

using Duality.Drawing;
using Duality.Resources;

using OpenTK.Graphics.OpenGL;

namespace Duality.Backend.DefaultOpenTK
{
	[DontSerialize]
	public class NativeShaderProgram : INativeShaderProgram
	{
		private	static NativeShaderProgram curBound = null;
		public static void Bind(NativeShaderProgram prog)
		{
			if (curBound == prog) return;

			if (prog == null)
			{
				GL.UseProgram(0);
				curBound = null;
			}
			else
			{
				GL.UseProgram(prog.Handle);
				curBound = prog;
			}
		}
		public static void SetUniform(ref ShaderFieldInfo field, int location, float[] data)
		{
			if (field.Scope != ShaderFieldScope.Uniform) return;
			if (location == -1) return;
			switch (field.Type)
			{
				case ShaderFieldType.Bool:
				case ShaderFieldType.Int:
					int[] arrI = new int[field.ArrayLength];
					for (int j = 0; j < arrI.Length; j++) arrI[j] = (int)data[j];
					GL.Uniform1(location, arrI.Length, arrI);
					break;
				case ShaderFieldType.Float:
					GL.Uniform1(location, data.Length, data);
					break;
				case ShaderFieldType.Vec2:
					GL.Uniform2(location, data.Length / 2, data);
					break;
				case ShaderFieldType.Vec3:
					GL.Uniform3(location, data.Length / 3, data);
					break;
				case ShaderFieldType.Vec4:
					GL.Uniform4(location, data.Length / 4, data);
					break;
				case ShaderFieldType.Mat2:
					GL.UniformMatrix2(location, data.Length / 4, false, data);
					break;
				case ShaderFieldType.Mat3:
					GL.UniformMatrix3(location, data.Length / 9, false, data);
					break;
				case ShaderFieldType.Mat4:
					GL.UniformMatrix4(location, data.Length / 16, false, data);
					break;
			}
		}

		private int handle;
		private ShaderFieldInfo[] fields;
		private int[] fieldLocations;

		public int Handle
		{
			get { return this.handle; }
		}
		public ShaderFieldInfo[] Fields
		{
			get { return this.fields; }
		}
		public int[] FieldLocations
		{
			get { return this.fieldLocations; }
		}

		void INativeShaderProgram.LoadProgram(IEnumerable<INativeShaderPart> shaderParts)
		{
			DefaultOpenTKBackendPlugin.GuardSingleThreadState();

			// Verify that we have exactly one shader part for each stage.
			// Other scenarios are valid in desktop GL, but not GL ES, so 
			// we'll enforce the stricter rules manually to ease portability.
			int vertexCount = 0;
			int fragmentCount = 0;
			foreach (INativeShaderPart part in shaderParts)
			{
				Resources.ShaderType type = (part as NativeShaderPart).Type;
				if (type == Resources.ShaderType.Fragment)
					fragmentCount++;
				else if (type == Resources.ShaderType.Vertex)
					vertexCount++;
			}
			if (vertexCount == 0) throw new ArgumentException("Cannot load program without vertex shader.");
			if (fragmentCount == 0) throw new ArgumentException("Cannot load program without fragment shader.");
			if (vertexCount > 1) throw new ArgumentException("Cannot attach multiple vertex shaders to the same program.");
			if (fragmentCount > 1) throw new ArgumentException("Cannot attach multiple fragment shaders to the same program.");

			// Create or reset GL program
			if (this.handle == 0) 
				this.handle = GL.CreateProgram();
			else
				this.DetachShaders();

			// Attach all individual shaders to the program
			foreach (INativeShaderPart part in shaderParts)
			{
				GL.AttachShader(this.handle, (part as NativeShaderPart).Handle);
			}

			// Link the shader program
			GL.LinkProgram(this.handle);

			int result;
			GL.GetProgram(this.handle, GetProgramParameterName.LinkStatus, out result);
			if (result == 0)
			{
				string errorLog = GL.GetProgramInfoLog(this.handle);
				this.RollbackAtFault();
				throw new BackendException(string.Format("Linker error:{1}{0}", errorLog, Environment.NewLine));
			}

			// Collect variable infos from sub programs
			HashSet<ShaderFieldInfo> fieldSet = new HashSet<ShaderFieldInfo>();
			foreach (INativeShaderPart item in shaderParts)
			{
				NativeShaderPart shaderPart = item as NativeShaderPart;
				for (int i = 0; i < shaderPart.Fields.Length; i++)
				{
					fieldSet.Add(shaderPart.Fields[i]);
				}
			}
			this.fields = fieldSet.ToArray();

			// Determine each variables location
			this.fieldLocations = new int[this.fields.Length];
			for (int i = 0; i < this.fields.Length; i++)
			{
				if (this.fields[i].Scope == ShaderFieldScope.Uniform)
					this.fieldLocations[i] = GL.GetUniformLocation(this.handle, this.fields[i].Name);
				else
					this.fieldLocations[i] = GL.GetAttribLocation(this.handle, this.fields[i].Name);
			}
		}
		ShaderFieldInfo[] INativeShaderProgram.GetFields()
		{
			return this.fields.Clone() as ShaderFieldInfo[];
		}
		void IDisposable.Dispose()
		{
			if (DualityApp.ExecContext == DualityApp.ExecutionContext.Terminated)
				return;

			this.DeleteProgram();
		}

		/// <summary>
		/// Given a vertex element declaration, this method selects, which of the shaders
		/// attribute fields best matches it, and returns the <see cref="Fields"/> index.
		/// Returns -1, if no match was found.
		/// </summary>
		/// <param name="element"></param>
		/// <returns></returns>
		public int SelectField(ref VertexElement element)
		{
			// Until there is any other indication, select fields purely by type.
			// This is error prone and fails when multiple vertex elements have the
			// same type and length.
			// ToDo: Replace this legacy solution with something reasonable.
			for (int i = 0; i < this.fields.Length; i++)
			{
				// Skip invalid and non-attribute fields
				if (this.fieldLocations[i] == -1) continue;
				if (this.fields[i].Scope != ShaderFieldScope.Attribute) continue;
				
				// Skip fields that do not match the specified element type
				Type elementPrimitive = this.fields[i].Type.GetElementPrimitive();
				Type requiredPrimitive = null;
				switch (element.Type)
				{
					case VertexElementType.Byte:
						requiredPrimitive = typeof(byte);
						break;
					case VertexElementType.Float:
						requiredPrimitive = typeof(float);
						break;
				}
				if (elementPrimitive != requiredPrimitive)
					continue;

				// Skip fields that do not match the required array length / primitive element count
				int elementCount = this.fields[i].Type.GetElementCount();
				if (element.Count != elementCount * this.fields[i].ArrayLength)
					continue;

				// Select the first matching field;
				return i;
			}

			return -1;
		}

		private void DeleteProgram()
		{
			if (this.handle == 0) return;

			this.DetachShaders();
			GL.DeleteProgram(this.handle);
			this.handle = 0;
		}
		private void DetachShaders()
		{
			// Determine currently attached shaders
			int[] attachedShaders = new int[10];
			int attachCount = 0;
			GL.GetAttachedShaders(this.handle, attachedShaders.Length, out attachCount, attachedShaders);

			// Detach all attached shaders
			for (int i = 0; i < attachCount; i++)
			{
				GL.DetachShader(this.handle, attachedShaders[i]);
			}
		}
		/// <summary>
		/// In case of errors loading the program, this methods rolls back the state of this
		/// shader program, so consistency can be assured.
		/// </summary>
		private void RollbackAtFault()
		{
			this.fields = new ShaderFieldInfo[0];
			this.fieldLocations = new int[0];

			this.DeleteProgram();
		}
	}
}
